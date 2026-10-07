using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using NetRpcPerformance;

namespace NetRpcTransportBaseline;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" })) { SelfTests.Run(); return 0; }
            var options = Options.Parse(args);
            Directory.CreateDirectory(options.Output);
            var results = new List<CaseResult>();
            var failures = new List<object>();
            var environment = new
            {
                Scope = "Transport-only diagnostic; all four native endpoints in ONE process. Not the complete NetRpc module or per-role allocation.",
                Topology = "Two Host-Client connections, four directed sends per tick: Client0->Host, Client1->Host, Host->Client0, Host->Client1.",
                Framework = RuntimeInformation.FrameworkDescription, Runtime = Environment.Version.ToString(),
                OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = Environment.ProcessId, ProcessorCount = Environment.ProcessorCount,
                ServerGc = GCSettings.IsServerGC, LatencyMode = GCSettings.LatencyMode.ToString(),
                StopwatchFrequency = Stopwatch.Frequency,
                TransportFramework = typeof(TcpTransport).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
                StartedUtc = DateTimeOffset.UtcNow,
                AllocationMeaning = "GC.GetTotalAllocatedBytes(true): every managed thread in this process, including transport, callbacks, scheduler and harness; no subtraction.",
                ByteMeaning = "Application payload bytes from completed sends and actual receive callbacks, not TCP/IP or authenticated-UDP framing bytes.",
                WindowMeaning = "After admission/proof/warmup/drain; includes producer and bounded receive completion fence. Post-window quiet drain and report/disposal excluded.",
                RateMeaning = "Requested ticks/second. Four sends per tick for tcp/udp. Control uses identical pacing/input mutation with zero sends.",
                WarmupTickRate = 1000,
                Options = options
            };
            File.WriteAllText(Path.Combine(options.Output, "environment.json"), JsonSerializer.Serialize(environment, Json));
            foreach (int rate in options.Rates)
            foreach (string channel in options.Channels)
            foreach (int size in channel == "control" ? new[] { options.Sizes[0] } : options.Sizes)
            for (int repeat = 1; repeat <= options.Repeats; repeat++)
            {
                string name = $"{channel}-{size}B-{rate}ticks-r{repeat}";
                try
                {
                    CaseResult result = await RunCase(options, channel, size, rate, repeat);
                    results.Add(result);
                    File.WriteAllText(Path.Combine(options.Output, name + ".json"), JsonSerializer.Serialize(result, Json));
                    Console.WriteLine($"{name}: {(result.Valid ? "PASS" : "FAIL")} allocated={result.AllocatedBytes}B sends={result.CompletedSends} receives={result.UniqueMeasuredReceives} missing={result.MissingReceivesAtWindowEnd} faults={result.ProtocolFaults} seconds={result.Seconds:F6}");
                }
                catch (Exception error)
                {
                    var failure = new { Case = name, Error = error.ToString(), AtUtc = DateTimeOffset.UtcNow };
                    failures.Add(failure);
                    File.WriteAllText(Path.Combine(options.Output, name + "-failure.json"), JsonSerializer.Serialize(failure, Json));
                    Console.Error.WriteLine($"{name}: {error}");
                }
            }
            File.WriteAllText(Path.Combine(options.Output, "suite.json"), JsonSerializer.Serialize(new { Environment = environment, Results = results, Failures = failures }, Json));
            return failures.Count == 0 && results.All(r => r.Valid) ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async UniTask<CaseResult> RunCase(Options options, string channel, int size, int rate, int repeat)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30 + options.Iterations / (double)rate + options.ReceiveTimeoutMs / 1000d));
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        var transports = new TcpTransport[4];
        var faults = new FaultCounter();
        try
        {
            // Indices are sending edges. Opposite receiver: 0<->2, 1<->3.
            for (int client = 0; client < 2; client++)
            {
                var accepting = listener.AcceptAsync(TimeSpan.FromSeconds(5), deadline.Token);
                transports[client] = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port, cancellationToken: deadline.Token);
                transports[client + 2] = await accepting;
            }
            foreach (var transport in transports) { transport.Faulted += faults.OnFault; transport.Closed += faults.OnClosed; }
            WaitUntilReady(transports, deadline.Token);
            var observers = new Receiver[4];
            var payloads = new byte[4][];
            var completed = new long[4];
            for (int edge = 0; edge < 4; edge++)
            {
                observers[edge] = new Receiver(edge, size, options.Warmup, options.Iterations);
                transports[(edge + 2) % 4].OnReceived += observers[edge].Receive;
                payloads[edge] = Payload.Create(edge, size);
            }
            var pacer = new RatePacer(rate);
            // Real network warmup for control too, then the same warmed sockets stay open and idle.
            // Pace the warmup too: an unbounded UDP burst can overflow socket buffers before measurement.
            var warmupPacer = new RatePacer(1000);
            warmupPacer.Reset();
            await RunLoop(transports, payloads, completed, options.Warmup, channel == "control" ? "tcp" : channel, 0, warmupPacer, deadline.Token);
            if (!WaitForReceives(observers, options.Warmup, false, options.ReceiveTimeoutMs, deadline.Token))
                throw new InvalidDataException($"Warmup delivery timeout: {string.Join(",", observers.Select(o => o.WarmupUnique))} of {options.Warmup} per edge.");
            Thread.Sleep(options.DrainMs);
            if (faults.Count != 0 || faults.Closed != 0 || observers.Any(o => !o.CleanWarmup))
                throw new InvalidDataException("Warmup fault, closed connection, duplicate, or payload validation failure.");
            Array.Clear(completed);
            ReceiverSnapshot[] beforeWindow = observers.Select(o => o.Snapshot()).ToArray();
            // Prime measurement/snapshot JIT before opening the allocation window.
            _ = Stamp.Capture();
            foreach (var observer in observers) _ = observer.Snapshot();
            pacer.Reset();
            Stamp start = Stamp.Capture();
            await RunLoop(transports, payloads, completed, options.Iterations, channel, 1, pacer, deadline.Token);
            long producerEnd = Stopwatch.GetTimestamp();
            bool delivered = channel == "control" || WaitForReceives(observers, options.Iterations, true, options.ReceiveTimeoutMs, deadline.Token);
            Stamp end = Stamp.Capture();
            // Everything allocated below is outside the measurement window.
            ReceiverSnapshot[] received = observers.Select(o => o.Snapshot()).ToArray();
            Thread.Sleep(options.DrainMs);
            ReceiverSnapshot[] afterDrain = observers.Select(o => o.Snapshot()).ToArray();
            long sends = completed.Sum(), unique = received.Sum(r => r.Unique);
            long invalid = received.Sum(r => r.Invalid), duplicates = received.Sum(r => r.Duplicates);
            long callbacks = received.Sum(r => r.TotalCallbacks) - beforeWindow.Sum(r => r.TotalCallbacks);
            long receivedBytes = received.Sum(r => r.TotalPayloadBytes) - beforeWindow.Sum(r => r.TotalPayloadBytes);
            double seconds = (end.Timestamp - start.Timestamp) / (double)Stopwatch.Frequency;
            long allocated = end.Allocated - start.Allocated;
            return new CaseResult
            {
                Channel = channel, PayloadSize = size, TickRate = rate, Repeat = repeat, Iterations = options.Iterations,
                Warmup = options.Warmup, RequestedSendsPerSecond = channel == "control" ? 0 : 4 * rate,
                ExpectedSends = channel == "control" ? 0 : 4L * options.Iterations,
                CompletedSends = sends, SentPayloadBytes = sends * size,
                ReceiveCallbacks = callbacks, UniqueMeasuredReceives = unique, ReceivedPayloadBytes = receivedBytes,
                MissingReceivesAtWindowEnd = sends - unique, ReceiveCompletionTimedOut = !delivered,
                InvalidPayloads = invalid, DuplicateReceives = duplicates, ProtocolFaults = faults.Count,
                UnexpectedCloses = faults.Closed, FirstProtocolFault = faults.FirstError?.ToString(),
                Start = start, End = end, AllocatedBytes = allocated, Seconds = seconds,
                ProducerSeconds = (producerEnd - start.Timestamp) / (double)Stopwatch.Frequency,
                ReceiveFenceSeconds = (end.Timestamp - producerEnd) / (double)Stopwatch.Frequency,
                Gen0 = end.Gen0 - start.Gen0, Gen1 = end.Gen1 - start.Gen1, Gen2 = end.Gen2 - start.Gen2,
                AllocatedBytesPerSecond = allocated / seconds,
                BytesPerCompletedSend = sends == 0 ? null : allocated / (double)sends,
                BytesPerUniqueReceive = unique == 0 ? null : allocated / (double)unique,
                CompletedSendsPerSecond = sends / seconds, UniqueReceivesPerSecond = unique / seconds,
                TickThroughput = options.Iterations / seconds,
                CompletedSendsByEdge = completed, ReceiversBeforeWindow = beforeWindow, ReceiversAtWindowEnd = received, ReceiversAfterDrain = afterDrain,
                LateReceivesDuringExcludedDrain = afterDrain.Sum(r => r.Unique) - unique,
                Valid = sends == (channel == "control" ? 0 : 4L * options.Iterations) && delivered && unique == sends
                    && callbacks == unique && invalid == 0 && duplicates == 0 && faults.Count == 0 && faults.Closed == 0
                    && afterDrain.Sum(r => r.TotalCallbacks) == received.Sum(r => r.TotalCallbacks)
                    && afterDrain.All(r => r.WarmupInvalid == 0)
            };
        }
        finally
        {
            // Disposal is outside the window; exceptions are not swallowed by this diagnostic.
            foreach (var transport in transports)
                if (transport != null) await transport.DisposeUniTaskAsync();
        }
    }

    internal static async UniTask RunLoop(ITransport[] transports, byte[][] payloads, long[] completed, int iterations,
        string channel, byte phase, RatePacer? pacer, CancellationToken token)
    {
        for (int i = 0; i < iterations; i++)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                Payload.SetSequence(payloads[edge], phase, i);
                if (channel == "tcp") await transports[edge].Send(payloads[edge], token);
                else if (channel == "udp") await transports[edge].SendFast(payloads[edge], token);
                if (channel != "control") completed[edge]++;
            }
            pacer?.Wait();
        }
    }

    private static bool WaitForReceives(Receiver[] observers, int count, bool measured, int timeoutMs, CancellationToken token)
    {
        long end = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        while (true)
        {
            bool all = true;
            for (int i = 0; i < observers.Length; i++)
                if ((measured ? observers[i].MeasuredUnique : observers[i].WarmupUnique) < count) all = false;
            if (all) return true;
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= end) return false;
            Thread.Sleep(1);
        }
    }

    private static void WaitUntilReady(TcpTransport[] transports, CancellationToken token)
    {
        long end = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (transports.Any(t => !t.IsUnreliableReady))
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= end) throw new TimeoutException("Authenticated UDP endpoint proof did not complete.");
            Thread.Sleep(1);
        }
    }
}

internal static class Payload
{
    public static byte[] Create(int edge, int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = Pattern(edge, i);
        bytes[0] = 0xD7; bytes[1] = (byte)edge; bytes[2] = 0;
        SetSequence(bytes, 0, 0);
        return bytes;
    }
    public static byte Pattern(int edge, int offset) => unchecked((byte)(17 + edge * 37 + offset * 13));
    public static void SetSequence(byte[] bytes, byte phase, int sequence)
    { bytes[2] = phase; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), sequence); }
}

internal sealed class Receiver(int edge, int size, int warmup, int iterations)
{
    private readonly int[] _warmupSeen = new int[warmup], _seen = new int[iterations];
    private long _warmupUnique, _unique, _callbacks, _bytes, _invalid, _duplicates, _warmupInvalid, _totalCallbacks, _totalBytes;
    public long WarmupUnique => Interlocked.Read(ref _warmupUnique);
    public long MeasuredUnique => Interlocked.Read(ref _unique);
    public bool CleanWarmup => Interlocked.Read(ref _warmupInvalid) == 0 && Interlocked.Read(ref _invalid) == 0;

    public void Receive(ReadOnlyMemory<byte> memory)
    {
        ReadOnlySpan<byte> bytes = memory.Span;
        Interlocked.Increment(ref _totalCallbacks); Interlocked.Add(ref _totalBytes, bytes.Length);
        bool isWarmup = bytes.Length > 2 && bytes[2] == 0;
        if (!isWarmup) { Interlocked.Increment(ref _callbacks); Interlocked.Add(ref _bytes, bytes.Length); }
        bool valid = bytes.Length == size && bytes.Length >= 8 && bytes[0] == 0xD7 && bytes[1] == edge && bytes[2] <= 1;
        int sequence = valid ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4)) : -1;
        int[] seen = isWarmup ? _warmupSeen : _seen;
        valid &= sequence >= 0 && sequence < seen.Length;
        if (valid)
            for (int i = 3; i < bytes.Length; i++)
                if ((i < 4 || i >= 8) && bytes[i] != Payload.Pattern(edge, i)) { valid = false; break; }
        if (!valid) { if (isWarmup) Interlocked.Increment(ref _warmupInvalid); else Interlocked.Increment(ref _invalid); return; }
        if (Interlocked.Exchange(ref seen[sequence], 1) != 0)
        { if (isWarmup) Interlocked.Increment(ref _warmupInvalid); else Interlocked.Increment(ref _duplicates); return; }
        if (isWarmup) Interlocked.Increment(ref _warmupUnique); else Interlocked.Increment(ref _unique);
    }

    public ReceiverSnapshot Snapshot() => new(edge, WarmupUnique, Interlocked.Read(ref _callbacks), MeasuredUnique,
        Interlocked.Read(ref _bytes), Interlocked.Read(ref _invalid), Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _totalCallbacks), Interlocked.Read(ref _totalBytes), Interlocked.Read(ref _warmupInvalid));
}

internal sealed class FaultCounter
{
    private long _count, _closed;
    public Exception? FirstError;
    public long Count => Interlocked.Read(ref _count);
    public long Closed => Interlocked.Read(ref _closed);
    public void OnFault(Exception error) { Interlocked.CompareExchange(ref FirstError, error, null); Interlocked.Increment(ref _count); }
    public void OnClosed() => Interlocked.Increment(ref _closed);
}

internal readonly record struct Stamp(long Allocated, int Gen0, int Gen1, int Gen2, long Timestamp)
{
    public static Stamp Capture() => new(GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Stopwatch.GetTimestamp());
}

internal sealed record ReceiverSnapshot(int Edge, long WarmupUnique, long Callbacks, long Unique, long PayloadBytes, long Invalid, long Duplicates,
    long TotalCallbacks, long TotalPayloadBytes, long WarmupInvalid);

internal sealed class CaseResult
{
    public required string Channel { get; init; }
    public int PayloadSize { get; init; }
    public int TickRate { get; init; }
    public int Repeat { get; init; }
    public int Iterations { get; init; }
    public int Warmup { get; init; }
    public int RequestedSendsPerSecond { get; init; }
    public long ExpectedSends { get; init; }
    public long CompletedSends { get; init; }
    public long SentPayloadBytes { get; init; }
    public long ReceiveCallbacks { get; init; }
    public long UniqueMeasuredReceives { get; init; }
    public long ReceivedPayloadBytes { get; init; }
    public long MissingReceivesAtWindowEnd { get; init; }
    public bool ReceiveCompletionTimedOut { get; init; }
    public long InvalidPayloads { get; init; }
    public long DuplicateReceives { get; init; }
    public long ProtocolFaults { get; init; }
    public long UnexpectedCloses { get; init; }
    public string? FirstProtocolFault { get; init; }
    public Stamp Start { get; init; }
    public Stamp End { get; init; }
    public long AllocatedBytes { get; init; }
    public double Seconds { get; init; }
    public double ProducerSeconds { get; init; }
    public double ReceiveFenceSeconds { get; init; }
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
    public double AllocatedBytesPerSecond { get; init; }
    public double? BytesPerCompletedSend { get; init; }
    public double? BytesPerUniqueReceive { get; init; }
    public double CompletedSendsPerSecond { get; init; }
    public double UniqueReceivesPerSecond { get; init; }
    public double TickThroughput { get; init; }
    public required long[] CompletedSendsByEdge { get; init; }
    public required ReceiverSnapshot[] ReceiversBeforeWindow { get; init; }
    public required ReceiverSnapshot[] ReceiversAtWindowEnd { get; init; }
    public required ReceiverSnapshot[] ReceiversAfterDrain { get; init; }
    public long LateReceivesDuringExcludedDrain { get; init; }
    public bool Valid { get; init; }
}

internal sealed record Options(int Iterations, int Warmup, int Repeats, int[] Rates, int[] Sizes, string[] Channels, int ReceiveTimeoutMs, int DrainMs, string Output)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--iterations", "--warmup", "--repeats", "--rates", "--sizes", "--channels", "--receive-timeout-ms", "--drain-ms", "--output"];
        if (args.Length % 2 != 0) throw new ArgumentException("Options are --name value pairs (or --self-test alone).");
        for (int i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Unknown or duplicate option: {args[i]}");
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback, int min, int max) => ParseNumber(Get(key, fallback.ToString()), min, max);
        int[] Numbers(string key, string fallback, int min, int max) => Get(key, fallback).Split(',').Select(s => ParseNumber(s, min, max)).Distinct().ToArray();
        string[] channels = Get("--channels", "control,tcp,udp").Split(',');
        if (channels.Length == 0 || channels.Distinct().Count() != channels.Length || channels.Any(s => s != "tcp" && s != "udp" && s != "control"))
            throw new ArgumentException("Channels must be a unique comma-separated selection of control,tcp,udp.");
        return new(Number("--iterations", 600, 1, 1000000), Number("--warmup", 256, 1, 1000000), Number("--repeats", 3, 1, 100),
            Numbers("--rates", "60,300", 1, 10000), Numbers("--sizes", "32,256,1024", 8, 60000), channels,
            Number("--receive-timeout-ms", 3000, 1, 60000), Number("--drain-ms", 100, 1, 10000),
            Get("--output", Path.Combine("Artifacts", "NetRpcTransportBaseline", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))));
    }
    private static int ParseNumber(string value, int min, int max) => int.TryParse(value, out int number) && number >= min && number <= max
        ? number : throw new ArgumentException($"Expected integer {min}..{max}: {value}");
}
