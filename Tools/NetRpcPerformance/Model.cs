using Cysharp.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BITKit.Multiplayer;
using BITKit.Multiplayer.NetRpc;
using NetRpcPerformance.Contracts;
using ITransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace NetRpcPerformance;

public sealed class Actor
{
    public int Calls;
    [Rpc(SendTo.Host)] public void Notify(int value) { if (value != 1) throw new InvalidOperationException(); Interlocked.Increment(ref Calls); }
}

public sealed class Workload : IWorkload
{
    private readonly Actor _actor;
    public Workload(Actor actor) => _actor = actor;
    public long Calls;
    public int ScalarState { get; set; }
    public IList<int> Items { get; } = new NetworkList<int>();
    public IDictionary<int, int> Counts { get; } = new NetworkDictionary<int, int>();
    public UniTask<int> Scalar(int value) { Interlocked.Increment(ref Calls); return UniTask.FromResult(value); }
    public UniTask<int> Dto(BoundedDto value) { ValidatePayload(value.Payload); Interlocked.Increment(ref Calls); return UniTask.FromResult(value.Sequence + value.Payload.Length); }
    public UniTask<int> Bytes(byte[] value) { ValidatePayload(value); Interlocked.Increment(ref Calls); return UniTask.FromResult(value.Length); }
    private static void ValidatePayload(byte[] value) { if (value.Length == 0 || value[0] != 42 || value[^1] != 42) throw new InvalidOperationException("Invalid bounded payload contents."); }
    public UniTask<int> Fence() => UniTask.FromResult(Volatile.Read(ref _actor.Calls));
}

// Transport extension point intentionally outside Core. A future adapter can implement
// admission here without coupling the workload or report schema to its library.
public interface IBenchmarkTransportProvider
{
    string Name { get; }
    UniTask<IBenchmarkConnection> ConnectAsync(int port, CancellationToken token);
    IBenchmarkListener Listen();
}
public interface IBenchmarkConnection : ITransport, ITransportLifetime, IAsyncDisposable
{
    bool IsUnreliableReady { get; }
}
public interface IBenchmarkListener : IDisposable
{
    int Port { get; }
    UniTask<IBenchmarkConnection> AcceptAsync();
}
public sealed class NativeTransportProvider : IBenchmarkTransportProvider
{
    public string Name => "native-tcp-udp";
    public async UniTask<IBenchmarkConnection> ConnectAsync(int port, CancellationToken token) => new NativeConnection(await TcpTransport.ConnectAsync("127.0.0.1", port, cancellationToken: token));
    public IBenchmarkListener Listen() => new NativeListener();
    private sealed class NativeListener : IBenchmarkListener
    {
        private readonly TcpTransportListener _listener = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        public int Port => _listener.EndPoint.Port;
        public async UniTask<IBenchmarkConnection> AcceptAsync() => new NativeConnection(await _listener.AcceptAsync());
        public void Dispose() => _listener.Dispose();
    }
    private sealed class NativeConnection(TcpTransport transport) : IBenchmarkConnection
    {
        public bool IsUnreliableReady => transport.IsUnreliableReady;
        public event Action<ReadOnlyMemory<byte>>? OnReceived { add => transport.OnReceived += value; remove => transport.OnReceived -= value; }
        public event Action? Closed { add => transport.Closed += value; remove => transport.Closed -= value; }
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken token = default) => transport.Send(payload, token);
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken token = default) => transport.SendFast(payload, token);
        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }
}

public sealed class CountingTransport : ITransport, ITransportLifetime
{
    private readonly ITransport _inner;
    public long SentBytes, ReceivedBytes, ReliableSent, FastSent, ReceivedFrames, ComponentFrames;
    public int Returns;
    public event Action<ReadOnlyMemory<byte>>? OnReceived;
    public event Action? Closed;
    public CountingTransport(ITransport inner)
    {
        _inner = inner;
        inner.OnReceived += Receive;
        if (inner is ITransportLifetime life) life.Closed += () => Closed?.Invoke();
    }
    private void Receive(ReadOnlyMemory<byte> bytes)
    {
        Interlocked.Add(ref ReceivedBytes, bytes.Length); Interlocked.Increment(ref ReceivedFrames);
        if (bytes.Length > 0 && bytes.Span[0] == (byte)NetRpcMessageKind.Component) Interlocked.Increment(ref ComponentFrames);
        if (bytes.Length > 0 && bytes.Span[0] == (byte)NetRpcMessageKind.Return) Interlocked.Increment(ref Returns);
        OnReceived?.Invoke(bytes);
    }
    public async UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _inner.Send(payload, cancellationToken);
        Interlocked.Add(ref SentBytes, payload.Length); Interlocked.Increment(ref ReliableSent);
    }
    public async UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _inner.SendFast(payload, cancellationToken);
        Interlocked.Add(ref SentBytes, payload.Length); Interlocked.Increment(ref FastSent);
    }
    public void Reset() { SentBytes = ReceivedBytes = ReliableSent = FastSent = ReceivedFrames = ComponentFrames = 0; Returns = 0; }
}

public readonly record struct Stamp(long Allocated, long Ticks, int Gen0, int Gen1, int Gen2)
{
    public static Stamp Take() => new(GC.GetTotalAllocatedBytes(true), Stopwatch.GetTimestamp(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}
public sealed record NodeReport(string Role, int Pid, string Profile, int Operations, long AllocatedBytes,
    double Seconds, int Gen0, int Gen1, int Gen2, long Completed, long Callbacks, long? SentBytes,
    long? ReceivedBytes, long? ReliableSends, long? UdpSends, long? ComponentFrames, long FinalRevision,
    int FinalValue, bool Monotonic, double LatencyP50Ms, double LatencyP95Ms, string LatencyMeaning,
    string ProxyType, bool WovenReceiver, int ReturnFrames, string Runtime, string OS, string Architecture,
    int Processors, bool ServerGC, long StopwatchFrequency, Stamp Begin, Stamp End, string Machine,
    string TransportAssemblyFramework, int LatencySamples, int OrdinaryBodyCalls);

public static class Statistics
{
    public static double Percentile(long[] samples, int count, double fraction)
    {
        if (count == 0) return 0;
        var sorted = samples.AsSpan(0, count).ToArray(); Array.Sort(sorted);
        return sorted[(int)Math.Ceiling(count * fraction) - 1] * 1000.0 / Stopwatch.Frequency;
    }
    public static bool IsWoven => typeof(Actor).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Any(m => m.Name.StartsWith("__netrpc_recv_", StringComparison.Ordinal))
        && typeof(Actor).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Any(m => m.Name.StartsWith("__netrpc_body_", StringComparison.Ordinal));
    public static string Runtime => RuntimeInformation.FrameworkDescription;
}
