using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Reflection;
using System.Security.Cryptography;

// External process: instrumentation and TraceLog conversion allocations never enter the target.
if (args.Length != 6 || args[0] != "--pid" || !int.TryParse(args[1], out var pid) || pid <= 0 ||
    args[2] != "--duration" || !int.TryParse(args[3], out var seconds) || seconds is < 1 or > 600 ||
    args[4] != "--outfile")
{
    Console.Error.WriteLine("Usage: dotnet run --project Tools/Performance/Arena.Performance.csproj -- --pid PID --duration SECONDS --outfile PATH.json");
    return 2;
}

var output = Path.GetFullPath(args[5]);
if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("--outfile must end in .json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var raw = Path.ChangeExtension(output, ".nettrace");
var sw = Stopwatch.StartNew();
var targetModules = TargetModules(pid);
var providers = new[]
{
    // GC keyword 0x1 (allocation ticks and GC suspension/restart); rundown resolves managed method IDs.
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x1)
};
var client = new DiagnosticsClient(pid);
using var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);
var startedUtc = DateTimeOffset.UtcNow;
DateTimeOffset stopRequestedUtc;
Console.WriteLine($"TRACE_STARTED pid={pid} utc={startedUtc:O} raw={raw}");
// Consume the pipe for the entire interval to avoid losing a bounded-buffer tail.
using (var file = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.Read))
{
    var copy = session.EventStream.CopyToAsync(file);
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    stopRequestedUtc = DateTimeOffset.UtcNow;
    Console.WriteLine($"TRACE_STOP_REQUESTED utc={stopRequestedUtc:O}");
    session.Stop(); // flush rundown BEFORE the targets can exit
    await copy;
}
var stoppedUtc = DateTimeOffset.UtcNow;
var groups = new Dictionary<string, Bucket>(StringComparer.Ordinal);
var types = new Dictionary<string, Bucket>(StringComparer.Ordinal);
var methods = new Dictionary<string, Bucket>(StringComparer.Ordinal);
var externalMethods = new Dictionary<string, Bucket>(StringComparer.Ordinal);
var allSuspensions = new List<Pause>();
var gcPauses = new List<double>();
var unresolved = 0L;
var samples = 0L;
var estimated = 0L;
var lost = 0L;
var suspended = new Dictionary<int, Suspension>();
var orphanRestarts = 0;
var skippedBeforeWindow = 0L;
var skippedAfterWindow = 0L;
var eventNames = new Dictionary<string, int>();
// Converting EventPipe to ETLX runs the rundown parser and resolves code addresses to managed methods.
// ETLX is an intermediate artifact kept alongside the original trace for independent verification.
var etlx = Path.ChangeExtension(output, ".etlx");
TraceLog.CreateFromEventPipeDataFile(raw, etlx);
using (var trace = new TraceLog(etlx))
{
    foreach (TraceEvent ev in trace.Events)
    {
        if (ev.ProcessID != pid) continue;
        var timestamp = new DateTimeOffset(ev.TimeStamp.ToUniversalTime());
        if (timestamp < startedUtc) { skippedBeforeWindow++; continue; }
        if (timestamp > stopRequestedUtc) { skippedAfterWindow++; continue; }
        var name = ev.EventName;
        eventNames[name] = eventNames.GetValueOrDefault(name) + 1;
        if (name.Contains("EventSourceMessage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("EventsLost", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Lost", StringComparison.OrdinalIgnoreCase)) lost++;
        }
        if (name is "GC/SuspendEEStart" or "GCSuspendEEStart")
        {
            // TraceEvent's CLR parser defines GCSuspendEEReason; unknown payloads are NOT GC.
            var reason = ev is GCSuspendEETraceData startEvent
                ? startEvent.Reason.ToString() : TextPayload(ev, "Reason") ?? "Unknown";
            suspended[ev.ThreadID] = new Suspension(ev.TimeStampRelativeMSec, reason);
        }
        else if (name is "GC/RestartEEStop" or "GCRestartEEStop")
        {
            // Suspension is process-wide and a restart can be emitted by another thread.
            // Never invent a start if no matched start exists inside the measurement window.
            if (suspended.Count == 0) { orphanRestarts++; continue; }
            var start = suspended.TryGetValue(ev.ThreadID, out var sameThread)
                ? sameThread : suspended.Values.MinBy(s => s.StartMilliseconds)!;
            var duration = Math.Max(0, ev.TimeStampRelativeMSec - start.StartMilliseconds);
            allSuspensions.Add(new Pause(start.Reason, duration));
            if (start.Reason is nameof(GCSuspendEEReason.SuspendForGC) or nameof(GCSuspendEEReason.SuspendForGCPrep))
                gcPauses.Add(duration);
            suspended.Clear();
        }
        if (name is not ("GC/AllocationTick" or "GCAllocationTick")) continue;
        samples++;
        long bytes = LongPayload(ev, "AllocationAmount64", "AllocationAmount");
        estimated += bytes;
        var type = TextPayload(ev, "TypeName") ?? "<unknown type>";
        var stack = new List<string>();
        for (var frame = ev.CallStack(); frame != null; frame = frame.Caller)
        {
            var method = frame.CodeAddress?.FullMethodName;
            if (!string.IsNullOrWhiteSpace(method)) stack.Add(method);
        }
        if (stack.Count == 0) unresolved++;
        var owner = stack.FirstOrDefault(IsProjectMethod) ?? "<external / no project frame>";
        var subsystem = Subsystem(owner);
        if (owner == "<external / no project frame>")
            Add(externalMethods, stack.FirstOrDefault() ?? "<no managed frame>", bytes, stack);
        Add(groups, subsystem, bytes, stack);
        Add(types, type, bytes, stack);
        // Inclusive stack: one allocation sample can appear in multiple methods. Owner buckets above
        // are exclusive; inclusive method buckets must NEVER be added up into a total.
        foreach (var method in stack.Distinct(StringComparer.Ordinal).Where(IsProjectMethod))
            Add(methods, method, bytes, stack);
    }
}
var pauses = gcPauses.Order().ToArray();
var allDurations = allSuspensions.Select(p => p.DurationMilliseconds).Order().ToArray();
var result = new
{
    Pid = pid, StartedUtc = startedUtc, StoppedUtc = stopRequestedUtc, StopRequestedUtc = stopRequestedUtc, TraceFlushedUtc = stoppedUtc,
    RequestedSeconds = seconds, ActualWallSeconds = (stopRequestedUtc - startedUtc).TotalSeconds,
    BoundaryRule = "Only target event timestamps within [StartedUtc, StopRequestedUtc] are analyzed; rundown/flush after StopRequestedUtc is retained in raw trace solely for managed-stack resolution. Intervals crossing either boundary are excluded.",
    SkippedTargetEventsBeforeWindow = skippedBeforeWindow, SkippedTargetEventsAfterWindow = skippedAfterWindow,
    TargetModules = targetModules,
    RawNettrace = raw, ResolvedEtlx = etlx, RawBytes = new FileInfo(raw).Length,
    Sampling = "GCAllocationTick estimated AllocationAmount64/AllocationAmount; sampled allocations, not exact object counts or exact total allocated bytes. No CPU or wall-time sampling.",
    AllocationSamples = samples, EstimatedSampledBytes = estimated, SamplesWithoutResolvedStack = unresolved,
    ExclusiveSubsystems = Rank(groups), SampledTypes = Rank(types), TopInclusiveMethods = Rank(methods),
    ExternalSamplesByLeafMethod = Rank(externalMethods),
    AllRuntimeSuspensions = new { Count = allDurations.Length, TotalMilliseconds = allDurations.Sum(),
        MaxMilliseconds = allDurations.DefaultIfEmpty().Max(), ByReason = allSuspensions.GroupBy(p => p.Reason)
            .OrderBy(p => p.Key).ToDictionary(g => g.Key, g => new { Count = g.Count(), TotalMilliseconds = g.Sum(p => p.DurationMilliseconds) }),
        DurationsMilliseconds = allDurations },
    GcPauses = new { Count = pauses.Length, TotalMilliseconds = pauses.Sum(), MaxMilliseconds = pauses.DefaultIfEmpty().Max(),
        P50Milliseconds = Percentile(pauses, .5), P95Milliseconds = Percentile(pauses, .95),
        DurationsMilliseconds = pauses, UnmatchedSuspensions = suspended.Count, OrphanRestarts = orphanRestarts,
        Definition = "Only complete GCSuspendEEStart (TraceEvent GCSuspendEEReason.SuspendForGC or SuspendForGCPrep) -> GCRestartEEStop intervals inside the measurement bounds; NOT other runtime suspensions or GCStart -> GCEnd/background duration." },
    DroppedEventSignals = lost,
    DropLimitations = "DroppedEventSignals counts observable lost-event markers only; an absent marker cannot prove zero loss. Compare target GC.GetTotalAllocatedBytes diagnostics. Trace buffer 256 MB; startup/rundown and boundary events may be missed.",
    ObservedEventNames = eventNames.Where(p => p.Key.Contains("GC", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("Lost", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Key).ToDictionary()
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"TRACE_DONE {output} samples={samples} resolved={samples - unresolved} pauses={pauses.Length} elapsed={sw.Elapsed}");
return 0;

static long LongPayload(TraceEvent ev, params string[] keys)
{
    foreach (var key in keys)
    {
        try { var value = ev.PayloadByName(key); if (value != null) return Convert.ToInt64(value); }
        catch (ArgumentException) { }
    }
    return 0;
}
static string? TextPayload(TraceEvent ev, string key)
{
    try { return ev.PayloadByName(key)?.ToString(); }
    catch (ArgumentException) { return null; }
}
static bool IsProjectMethod(string name) => name.StartsWith("BITKit.Multiplayer.", StringComparison.Ordinal) ||
    name.StartsWith("BITKit.Multiplayer+", StringComparison.Ordinal);
static string Subsystem(string name)
{
    if (name == "<external / no project frame>") return name;
    foreach (var part in new[] { "Multiplayer.Runtime", "Multiplayer" })
        if (name.Contains(part, StringComparison.Ordinal)) return part;
    return "Project / other";
}
static void Add(Dictionary<string, Bucket> map, string name, long bytes, List<string> stack)
{
    if (!map.TryGetValue(name, out var bucket)) map[name] = bucket = new Bucket();
    bucket.Samples++; bucket.EstimatedBytes += bytes;
    if (stack.Count != 0) bucket.ResolvedSamples++;
    if (stack.Count != 0 && bytes > bucket.RepresentativeSampleBytes)
    {
        bucket.RepresentativeSampleBytes = bytes;
        bucket.RepresentativeStack = stack.ToArray(); // full managed stack, leaf first
    }
}
static object[] Rank(Dictionary<string, Bucket> map) => map.OrderByDescending(kv => kv.Value.EstimatedBytes)
    .Take(40).Select(kv => (object)new { Name = kv.Key, kv.Value.Samples, kv.Value.EstimatedBytes, kv.Value.ResolvedSamples,
        kv.Value.RepresentativeSampleBytes, kv.Value.RepresentativeStack }).ToArray();
static double Percentile(double[] values, double quantile) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(quantile * values.Length) - 1];
static object TargetModules(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return process.Modules.Cast<ProcessModule>()
            .Where(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                m.ModuleName.Equals("libcoreclr.so", StringComparison.OrdinalIgnoreCase) ||
                m.ModuleName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                m.ModuleName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            .Select(m =>
            {
                using var stream = File.OpenRead(m.FileName);
                return new { m.ModuleName, m.FileName, Version = FileVersionInfo.GetVersionInfo(m.FileName).FileVersion,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
            }).ToArray();
    }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
    { return new { Unavailable = error.GetType().Name + ": " + error.Message }; }
}
sealed record Suspension(double StartMilliseconds, string Reason);
sealed record Pause(string Reason, double DurationMilliseconds);
sealed class Bucket
{
    public long Samples;
    public long EstimatedBytes;
    public long ResolvedSamples;
    public long RepresentativeSampleBytes;
    public string[]? RepresentativeStack;
}
