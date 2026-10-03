using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace BITKit.NetRpcTrace;

internal static class Program
{
    internal const string MarkerProvider = "BITKit-NetRpc-Benchmark";
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) { Guards.Run(); return 0; }
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("NetRpcTrace --input capture.nettrace --output report.json [--process-id PID] [--role ROLE] [--profile PROFILE] [--whole-trace] [--preview] [--max-stacks 100] [--max-depth 128]\nDefault: complete per-PID MeasurementStart/Stop windows only; --whole-trace explicitly permits unmarked traces. --self-test runs standalone guards.");
                return 0;
            }
            var options = Options.Parse(args);
            // TraceEvent 3.1.21 exposes the dispatcher overload, not ConvertEventPipeToTraceLogFile.
            // Conversion resolves managed JIT/rundown addresses before our two passes over events.
            var temp = Path.Combine(Path.GetTempPath(), "NetRpcTrace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var etlx = Path.Combine(temp, "capture.etlx");
                using (var pipe = new EventPipeEventSource(options.Input))
                    TraceLog.CreateFromEventTraceLogFile(pipe, etlx, new TraceLogOptions());
                using var log = new TraceLog(etlx);
                var report = Analyze(log, options);
                Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
                File.WriteAllText(options.Output, JsonSerializer.Serialize(report, JsonOptions));
                Console.WriteLine($"{report.Windows.Count} window(s); {report.Windows.Sum(w => w.Allocations.ValidAllocationSamples)} valid allocation samples; {report.Windows.Sum(w => w.Cpu.SampleCount)} profiler samples. Report: {options.Output}");
                if (options.Preview)
                    foreach (var w in report.Windows)
                    {
                        Console.WriteLine($"PID {w.ProcessId} {w.Role}/{w.Profile} [{w.StartMs:F3}, {w.StopMs:F3}) ms: weighted bytes {w.Allocations.WeightedBytes:N0}");
                        foreach (var type in w.Allocations.Types.Take(10)) Console.WriteLine($"  {type.Weight:N0} B sampled weight / {type.Samples} ticks: {type.Name}");
                        foreach (var stack in w.Allocations.Stacks.TopStacks.Take(6))
                        {
                            Console.WriteLine($"  ALLOC STACK weight={stack.Weight:N0} samples={stack.Samples}");
                            foreach (var frame in stack.FramesLeafFirst.Take(12)) Console.WriteLine("    " + (frame.Method ?? frame.Address));
                        }
                        foreach (var frame in w.Cpu.ManagedExclusiveLeafFrames.Take(10)) Console.WriteLine($"  {frame.Samples} managed profiler leaf samples: {frame.Name}");
                    }
                return 0;
            }
            finally { Directory.Delete(temp, recursive: true); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"NetRpcTrace: {ex.Message}"); return 1; }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Report Analyze(TraceLog log, Options options)
    {
        var markers = new List<Marker>();
        var processes = new Dictionary<int, (double First, double Last)>();
        long totalAllocations = 0, totalCpu = 0;
        foreach (var e in log.Events)
        {
            if (e.ProcessID <= 0) continue;
            var time = e.TimeStampRelativeMSec;
            processes[e.ProcessID] = processes.TryGetValue(e.ProcessID, out var range)
                ? (Math.Min(range.First, time), Math.Max(range.Last, time)) : (time, time);
            if (e is GCAllocationTickTraceData) totalAllocations++;
            if (e is ClrThreadSampleTraceData) totalCpu++;
            if (e.ProviderName != MarkerProvider || (int)e.ID is not (1 or 2)) continue;
            var payload = e.PayloadNames.ToDictionary(n => n, n => e.PayloadByName(n), StringComparer.OrdinalIgnoreCase);
            markers.Add(new Marker(e.ProcessID, (int)e.ID, e.EventName, time, e.TimeStamp.ToUniversalTime(), payload));
        }
        var windows = WindowBuilder.Build(markers);
        if (windows.Count == 0)
        {
            if (!options.WholeTrace) throw new InvalidDataException("No complete measurement marker pairs. Refusing startup/report contamination: use --whole-trace explicitly for an unmarked capture.");
            if (markers.Count != 0) throw new InvalidDataException("Incomplete measurement markers; --whole-trace cannot hide malformed/missing stop markers.");
            if (options.Role != null || options.Profile != null) throw new InvalidDataException("Role/profile filters require measurement markers.");
            windows.AddRange(processes.Select(p => new Window(p.Key, null, null, null, p.Value.First, Math.BitIncrement(p.Value.Last), true)));
        }
        windows = windows.Where(w => (options.ProcessId == null || w.ProcessId == options.ProcessId)
            && (options.Role == null || w.Role == options.Role) && (options.Profile == null || w.Profile == options.Profile)).ToList();
        if (windows.Count == 0) throw new InvalidDataException("No measurement windows match the requested PID/role/profile.");
        var accumulators = windows.Select(w => new WindowAccumulator(w, options)).ToList();
        foreach (var e in log.Events)
        {
            if (e is not GCAllocationTickTraceData && e is not ClrThreadSampleTraceData) continue;
            var accumulator = accumulators.FirstOrDefault(a => a.Window.Contains(e.ProcessID, e.TimeStampRelativeMSec));
            if (accumulator == null) continue;
            if (e is GCAllocationTickTraceData allocation) accumulator.AddAllocation(allocation);
            else accumulator.AddCpu((ClrThreadSampleTraceData)e);
        }
        return new Report(options.Input, typeof(TraceLog).Assembly.GetName().Version?.ToString(), log.EventsLost, log.Truncated,
            log.SampleProfileInterval.TotalMilliseconds, log.SessionStartTime.ToUniversalTime(), totalAllocations, totalCpu,
            markers, accumulators.Select(a => a.Finish()).ToList(),
            ["GCAllocationTick is sampled: AllocationAmount64 (v2+) or unsigned AllocationAmount (v0/v1) is accumulated allocation weight, NOT ObjectSize and NOT exact bytes per operation. A tick attributes intervening allocations to its sampled type/stack; boundaries can straddle the marker.",
             "Valid allocation samples require positive weight and a nonempty runtime TypeName. Invalid/untyped weights are retained separately, not assigned fabricated type names.",
             "Call stacks are leaf-first. Exclusive attribution uses only the actual leaf (never the first resolved ancestor). Inclusive frames count a method once per sample, even under recursion; inclusive totals are not additive.",
             "SampleProfiler samples are not precise CPU time: External samples may represent native execution or waiting. Managed/External/Error counts and per-frame sample kinds are retained. No CPU milliseconds are inferred from inter-sample gaps.",
             "TraceLog sampleProfileIntervalMs is reader metadata, not a guaranteed observed cadence. Observed per-thread timestamp deltas are reported separately; missing samples, scheduling, overhead and event loss bias results.",
             "Managed method names come only from CodeAddress.Method.FullMethodName resolved by captured JIT/rundown. No native symbol downloads or guessed UNKNOWN-to-method mappings. Unresolved addresses stay raw and are excluded from named frame tables.",
             "Half-open per-process marker windows [start,stop) exclude startup and final JSON/report output only if the producer emits markers at the proper measured boundaries. Whole-trace fallback is explicit and not a measured benchmark.",
             "Full-stack output is capped by --max-stacks; stack frames by --max-depth. Counts and named frame totals retain every sampled event; stack truncation and omitted groups are reported."]);
    }
}

internal sealed record Options(string Input, string Output, int? ProcessId, string? Role, string? Profile, bool WholeTrace, bool Preview, int MaxStacks, int MaxDepth)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--whole-trace" or "--preview") { if (!flags.Add(key)) throw new ArgumentException($"Duplicate {key}"); continue; }
            if (key is not ("--input" or "--output" or "--process-id" or "--role" or "--profile" or "--max-stacks" or "--max-depth")) throw new ArgumentException($"Unknown option {key}");
            if (++i == args.Length || args[i].StartsWith("--") || !values.TryAdd(key, args[i])) throw new ArgumentException($"Missing or duplicate value for {key}");
        }
        string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException($"Required: {key}");
        int Positive(string key, int fallback) => values.TryGetValue(key, out var value)
            ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new ArgumentException($"{key} must be positive") : fallback;
        var input = Path.GetFullPath(Required("--input"));
        var output = Path.GetFullPath(Required("--output"));
        if (!File.Exists(input)) throw new FileNotFoundException("Input trace not found", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must not overwrite input");
        return new Options(input, output, values.ContainsKey("--process-id") ? Positive("--process-id", 0) : null,
            values.GetValueOrDefault("--role"), values.GetValueOrDefault("--profile"), flags.Contains("--whole-trace"), flags.Contains("--preview"), Positive("--max-stacks", 100), Positive("--max-depth", 128));
    }
}

internal sealed record Marker(int ProcessId, int EventId, string EventName, double TimestampMs, DateTime TimestampUtc, Dictionary<string, object> Payload);
internal sealed record Window(int ProcessId, string? Role, string? Profile, int? Operations, double StartMs, double StopMs, bool WholeTrace)
{
    public bool Contains(int pid, double ms) => pid == ProcessId && ms >= StartMs && ms < StopMs;
}
internal static class WindowBuilder
{
    public static List<Window> Build(IEnumerable<Marker> markers)
    {
        var windows = new List<Window>();
        var active = new Dictionary<int, Marker>();
        foreach (var marker in markers.OrderBy(m => m.TimestampMs))
        {
            if (marker.EventId == 1)
            {
                if (!active.TryAdd(marker.ProcessId, marker)) throw new InvalidDataException($"Overlapping start markers for PID {marker.ProcessId}");
                continue;
            }
            if (!active.Remove(marker.ProcessId, out var start)) throw new InvalidDataException($"Stop without start for PID {marker.ProcessId}");
            string Text(string key) => start.Payload.TryGetValue(key, out var value) && value is string text && text.Length > 0 ? text : throw new InvalidDataException($"Start marker missing {key}");
            if (!start.Payload.TryGetValue("operations", out var operations) || !int.TryParse(Convert.ToString(operations, CultureInfo.InvariantCulture), out var count) || count < 0)
                throw new InvalidDataException("Start marker missing/invalid operations");
            if (marker.TimestampMs <= start.TimestampMs) throw new InvalidDataException("Measurement stop must follow start");
            windows.Add(new Window(start.ProcessId, Text("role"), Text("profile"), count, start.TimestampMs, marker.TimestampMs, false));
        }
        if (active.Count != 0) throw new InvalidDataException("Measurement start without stop");
        return windows;
    }
}

internal sealed record Report(string Input, string? TraceEventAssemblyVersion, int EventsLost, bool Truncated, double SampleProfileIntervalMs,
    DateTime SessionStartUtc, long TraceAllocationTicks, long TraceProfilerSamples, List<Marker> Markers, List<WindowReport> Windows, string[] Caveats);
internal sealed record Frame(string? Method, string Address, string? Module);
internal sealed record WeightedGroup(string Name, long Samples, long Weight, Dictionary<string, long> SampleKinds);
internal sealed record StackGroup(Frame[] FramesLeafFirst, long Samples, long Weight, bool Truncated);
internal sealed record StackSummary(long MissingStacks, long SamplesWithUnresolvedFrames, long UnresolvedFrameOccurrences, long UnresolvedLeafSamples,
    long DepthTruncatedSamples, int DistinctStackGroups, int OmittedStackGroups, List<StackGroup> TopStacks);
internal sealed record AllocationReport(long AllocationTicks, long ValidAllocationSamples, long WeightedBytes, long InvalidAllocationSamples, long InvalidWeightedBytes,
    Dictionary<int, long> EventVersions, List<WeightedGroup> Types, List<WeightedGroup> InclusiveFrames, List<WeightedGroup> ExclusiveLeafFrames, StackSummary Stacks);
internal sealed record IntervalReport(long DeltaCount, double? MinimumMs, double? MeanMs, double? MaximumMs, string Basis);
internal sealed record CpuReport(long SampleCount, Dictionary<string, long> SampleKinds, IntervalReport ObservedPerThreadIntervals,
    List<WeightedGroup> InclusiveFrames, List<WeightedGroup> ExclusiveLeafFrames,
    List<WeightedGroup> ManagedInclusiveFrames, List<WeightedGroup> ManagedExclusiveLeafFrames, StackSummary Stacks);
internal sealed record WindowReport(int ProcessId, string? Role, string? Profile, int? Operations, double StartMs, double StopMs, double DurationMs, bool WholeTrace,
    AllocationReport Allocations, CpuReport Cpu);

internal sealed class GroupTable
{
    private readonly Dictionary<string, (long Samples, long Weight, Dictionary<string, long> Kinds)> groups = new();
    public void Add(string name, long weight, string kind)
    {
        if (!groups.TryGetValue(name, out var row)) row = (0, 0, new());
        row.Samples++; row.Weight = checked(row.Weight + weight);
        row.Kinds[kind] = row.Kinds.GetValueOrDefault(kind) + 1;
        groups[name] = row;
    }
    public List<WeightedGroup> Finish() => groups.Select(p => new WeightedGroup(p.Key, p.Value.Samples, p.Value.Weight, p.Value.Kinds))
        .OrderByDescending(p => p.Weight).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
}

internal sealed class StackTable(int maxDepth, int maxStacks)
{
    private readonly Dictionary<string, StackGroup> stacks = new();
    private readonly GroupTable inclusive = new(), exclusive = new();
    private long missing, unresolvedSamples, unresolvedFrames, unresolvedLeaves, truncated;
    public void Add(TraceEvent e, long weight, string kind)
    {
        var frames = new List<Frame>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var stack = e.CallStack();
        if (stack == null) { missing++; return; }
        var unresolved = false;
        var depth = 0;
        for (var cursor = stack; cursor != null; cursor = cursor.Caller)
        {
            var address = cursor.CodeAddress;
            var name = address.Method?.FullMethodName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = null; unresolved = true; unresolvedFrames++;
                if (depth == 0) unresolvedLeaves++;
            }
            else
            {
                if (names.Add(name)) inclusive.Add(name, weight, kind);
                if (depth == 0) exclusive.Add(name, weight, kind);
            }
            if (depth < maxDepth) frames.Add(new Frame(name, $"0x{address.Address:x}", string.IsNullOrEmpty(address.ModuleName) ? null : address.ModuleName));
            depth++;
        }
        if (unresolved) unresolvedSamples++;
        var isTruncated = depth > maxDepth;
        if (isTruncated) truncated++;
        // Keep unresolved addresses separate; do not merge them into a fictitious named function.
        var key = JsonSerializer.Serialize(frames) + (isTruncated ? ":truncated" : "");
        if (stacks.TryGetValue(key, out var existing)) stacks[key] = existing with { Samples = existing.Samples + 1, Weight = checked(existing.Weight + weight) };
        else stacks.Add(key, new StackGroup(frames.ToArray(), 1, weight, isTruncated));
    }
    public List<WeightedGroup> Inclusive() => inclusive.Finish();
    public List<WeightedGroup> Exclusive() => exclusive.Finish();
    public StackSummary Finish() => new(missing, unresolvedSamples, unresolvedFrames, unresolvedLeaves, truncated, stacks.Count,
        Math.Max(0, stacks.Count - maxStacks), stacks.Values.OrderByDescending(s => s.Weight).ThenByDescending(s => s.Samples).Take(maxStacks).ToList());
}

internal sealed class WindowAccumulator(Window window, Options options)
{
    public Window Window { get; } = window;
    private long ticks, valid, bytes, invalid, invalidBytes, cpu;
    private readonly Dictionary<int, long> versions = new();
    private readonly Dictionary<string, long> kinds = new();
    private readonly Dictionary<int, double> lastThreadSample = new();
    private long deltas;
    private double deltaSum, deltaMin = double.PositiveInfinity, deltaMax;
    private readonly GroupTable types = new();
    private readonly StackTable allocationStacks = new(options.MaxDepth, options.MaxStacks), cpuStacks = new(options.MaxDepth, options.MaxStacks);
    public void AddAllocation(GCAllocationTickTraceData e)
    {
        ticks++; versions[e.Version] = versions.GetValueOrDefault(e.Version) + 1;
        var weight = e.Version >= 2 ? e.AllocationAmount64 : (long)(uint)e.AllocationAmount;
        if (weight <= 0 || string.IsNullOrWhiteSpace(e.TypeName)) { invalid++; if (weight > 0) invalidBytes = checked(invalidBytes + weight); return; }
        valid++; bytes = checked(bytes + weight);
        types.Add(e.TypeName, weight, e.AllocationKind.ToString());
        allocationStacks.Add(e, weight, e.AllocationKind.ToString());
    }
    public void AddCpu(ClrThreadSampleTraceData e)
    {
        cpu++; var kind = e.Type.ToString(); kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        if (lastThreadSample.TryGetValue(e.ThreadID, out var previous) && e.TimeStampRelativeMSec > previous)
        {
            var delta = e.TimeStampRelativeMSec - previous;
            deltas++; deltaSum += delta; deltaMin = Math.Min(deltaMin, delta); deltaMax = Math.Max(deltaMax, delta);
        }
        lastThreadSample[e.ThreadID] = e.TimeStampRelativeMSec;
        cpuStacks.Add(e, 1, kind);
    }
    private static List<WeightedGroup> ManagedOnly(List<WeightedGroup> frames) => frames
        .Where(f => f.SampleKinds.GetValueOrDefault("Managed") > 0)
        .Select(f => new WeightedGroup(f.Name, f.SampleKinds["Managed"], f.SampleKinds["Managed"], new() { ["Managed"] = f.SampleKinds["Managed"] }))
        .OrderByDescending(f => f.Samples).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
    public WindowReport Finish() => new(Window.ProcessId, Window.Role, Window.Profile, Window.Operations, Window.StartMs, Window.StopMs, Window.StopMs - Window.StartMs, Window.WholeTrace,
        new AllocationReport(ticks, valid, bytes, invalid, invalidBytes, versions, types.Finish(), allocationStacks.Inclusive(), allocationStacks.Exclusive(), allocationStacks.Finish()),
        new CpuReport(cpu, kinds, new IntervalReport(deltas, deltas == 0 ? null : deltaMin, deltas == 0 ? null : deltaSum / deltas, deltas == 0 ? null : deltaMax,
            "Positive timestamp deltas between successive profiler samples of the same PID/thread within this window; not CPU duration"), cpuStacks.Inclusive(), cpuStacks.Exclusive(),
            ManagedOnly(cpuStacks.Inclusive()), ManagedOnly(cpuStacks.Exclusive()), cpuStacks.Finish()));
}

internal static class Guards
{
    public static void Run()
    {
        Marker M(int pid, int id, double time) => new(pid, id, id == 1 ? "MeasurementStart" : "MeasurementStop", time, DateTime.UnixEpoch,
            id == 1 ? new(StringComparer.OrdinalIgnoreCase) { ["role"] = "host", ["profile"] = "test", ["operations"] = 10 } : new());
        var windows = WindowBuilder.Build([M(1, 1, 10), M(2, 1, 12), M(1, 2, 20), M(2, 2, 25), M(1, 1, 30), M(1, 2, 40)]);
        if (windows.Count != 3 || windows[0].Contains(2, 15) || windows[0].Contains(1, 9.9) || !windows[0].Contains(1, 10) || windows[0].Contains(1, 20) || windows[0].Operations != 10)
            throw new Exception("PID/half-open boundary or metadata guard failed");
        void Reject(Marker[] markers)
        {
            try { WindowBuilder.Build(markers); } catch (InvalidDataException) { return; }
            throw new Exception("Malformed markers were accepted");
        }
        Reject([M(1, 2, 20)]); Reject([M(1, 1, 10)]); Reject([M(1, 1, 10), M(1, 1, 11), M(1, 2, 20)]); Reject([M(1, 1, 10), M(1, 2, 10)]);
        Console.WriteLine("PASS: interleaved PID windows, repeated windows, start-inclusive/stop-exclusive boundaries, metadata, orphan/overlap/zero-duration marker rejection. Real EventPipe reading requires a captured trace.");
    }
}
