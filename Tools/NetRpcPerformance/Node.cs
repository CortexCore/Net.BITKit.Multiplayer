using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using NetRpcPerformance.Contracts;

namespace NetRpcPerformance;

public sealed class Node : IAsyncDisposable
{
    private readonly Options _options;
    private readonly string _role;
    private ServiceProvider? _services, _entityServices;
    private RpcContextService? _runtime;
    private CountingTransport? _counting;
    private IBenchmarkConnection? _socket;
    private readonly List<CountingTransport> _counters = new();
    private readonly List<IBenchmarkConnection> _hostSockets = new();
    private IBenchmarkListener? _listener;
    private RelayEndpoint? _relay;
    private RelayHostConnection? _sidecar;
    private IWorkload? _remote;
    private Workload? _local;
    private Actor? _actor;
    private readonly NetComponent<int> _component = new(1, 0);
    private long _callbacks, _previousRevision, _completed, _startCalls;
    private bool _monotonic = true, _measuring;
    private Stamp _start, _end;
    private readonly long[] _latency;
    private int _latencyCount;
    private Exception? _fault;
    private bool _relayInstrumented;
    private readonly byte[] _payload;
    private readonly BoundedDto _dto;
    private readonly RatePacer _pacer;
    private TransportTotals? _endTransport;
    private readonly record struct TransportTotals(long SentBytes, long ReceivedBytes, long ReliableSent, long FastSent, long ComponentFrames, int Returns);
    private string ReportRole => _role == "client" ? Options.ClientRole(_options.Clients, _options.ClientId) : _role;

    public Node(Options options, string role)
    {
        _options = options; _role = role;
        _latency = new long[Math.Max(options.Iterations, options.Warmup)];
        _payload = Enumerable.Repeat((byte)42, options.PayloadSize).ToArray();
        _dto = new BoundedDto { Sequence = 17, Payload = _payload };
        _pacer = new RatePacer(options.Rate);
    }
    public async Task Start()
    {
        if (_role == "relay")
        {
            _relay = new RelayEndpoint(new IPEndPoint(IPAddress.Loopback, 0), "performance-baseline");
            Console.WriteLine("READY " + _relay.EndPoint.Port); return;
        }
        if (!Statistics.IsWoven) throw new InvalidOperationException("Ordinary Actor has no actual woven receiver.");
        var services = new ServiceCollection().AddSingleton<IEntitiesService, EntitiesService>().AddNetRpcObject<Actor>();
        if (_role == "host")
        {
            services.AddNetRpcService<IWorkload, Workload>();
            // Register the current DI infrastructure, but keep publication explicit so
            // no timer silently coalesces individual measured changes.
            services.AddNetRpcRuntime(true, 713);
        }
        else
        {
            _socket = await new NativeTransportProvider().ConnectAsync(_options.Port, CancellationToken.None);
            _counting = new CountingTransport(_socket);
            _counters.Add(_counting);
            services.AddRemoteInterface<IWorkload>().AddNetRpcRuntime(false, 713);
        }
        _services = services.BuildServiceProvider(); _runtime = _services.GetRequiredService<RpcContextService>();
        _runtime.Faulted += error => Interlocked.CompareExchange(ref _fault, error, null);
        _actor = _services.GetRequiredService<Actor>();
        _entityServices = new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(1))
            .AddSingleton(_component).AddSingleton<INetComponent>(_component).BuildServiceProvider();
        _services.GetRequiredService<IEntitiesService>().Register(new NetEntity(_entityServices));
        _component.Changed += (_, _) => Changed(_component.Revision);
        if (_role == "host")
        {
            _local = _services.GetRequiredService<Workload>();
            for (int i = 0; i < _options.ContainerSize; i++) { _local.Items.Add(0); _local.Counts.Add(i, 0); }
            if (_options.Transport == "relay")
            {
                _sidecar = new RelayHostConnection(_runtime, "127.0.0.1", _options.Port, "performance-baseline");
                _sidecar.ConnectionFailed += error => Interlocked.CompareExchange(ref _fault, error, null);
                await Until(() => _sidecar.IsConnected);
                Console.WriteLine("READY 0");
            }
            else
            {
                _listener = new NativeTransportProvider().Listen();
                Console.WriteLine("READY " + _listener.Port);
                for (int i = 0; i < _options.Clients; i++)
                {
                    var socket = await _listener.AcceptAsync(); _hostSockets.Add(socket);
                    var counting = new CountingTransport(socket); _counters.Add(counting);
                    _runtime.AttachPeer((uint)(i + 2), counting);
                }
            }
        }
        else
        {
            // The runtime singleton is already cached before attachment can replay
            // frames. Remote proxy registration itself requests state from this peer.
            _runtime.AttachPeer(1, _counting!);
            _remote = _services.GetRequiredService<IWorkload>();
            if (!_remote.GetType().Name.StartsWith("NetRemote_", StringComparison.Ordinal))
                throw new InvalidOperationException("Not a generated native proxy: " + _remote.GetType());
            _runtime.StateChanged += (_, _) => Changed(_options.Profile == "syncvar" ? _remote.ScalarState : 0);
            ((NetworkCollection)_remote.Items).Changed += change => Changed(change.Revision);
            ((NetworkCollection)_remote.Counts).Changed += change => Changed(change.Revision);
            if (await _remote.Scalar(123) != 123) throw new InvalidOperationException("Generated receiver did not return expected value.");
            await Until(() => _remote.Items.Count == _options.ContainerSize && _remote.Counts.Count == _options.ContainerSize);
            await Until(() => _socket!.IsUnreliableReady);
            try { _component.Value = 1; throw new InvalidOperationException("Client component authority was not enforced."); }
            catch (BITKit.Multiplayer.RpcException) { }
            Console.WriteLine("READY 0");
        }
    }
    private void Changed(long revision)
    {
        if (!_measuring) return;
        var previous = Interlocked.Exchange(ref _previousRevision, revision);
        if (revision != 0 && revision <= previous) _monotonic = false;
        Interlocked.Increment(ref _callbacks);
    }
    public async Task Loop()
    {
        while (await Console.In.ReadLineAsync() is { } command)
        {
            var words = command.Split(' ');
            switch (words[0])
            {
                case "init":
                    if (_role == "host")
                    {
                        if (_sidecar != null) InstrumentRelayPeer();
                        await Until(() => _hostSockets.All(socket => socket.IsUnreliableReady));
                        await _runtime!.PublishStateAsync(true);
                        if (_options.Profile == "idle-sync") _runtime.StartSynchronization(new NetRpcOptions());
                    }
                    else if (_role == "client") await Until(() => _component.Revision >= 0);
                    Console.WriteLine("OK"); break;
                case "begin":
                    _callbacks = _completed = _previousRevision = 0; _monotonic = true; _latencyCount = 0;
                    foreach (var counter in _counters) counter.Reset();
                    _startCalls = _local?.Calls ?? _actor?.Calls ?? 0;
                    if (_options.Profile == "void" && _actor != null) _startCalls = _actor.Calls;
                    BenchmarkEvents.Log.MeasurementStart(ReportRole, _options.Profile, Options.HasNoBusinessOperations(_options.Profile) ? 0 : _options.Iterations);
                    _measuring = true; _start = Stamp.Take(); Console.WriteLine("OK"); break;
                case "run":
                    await Run(int.Parse(words[1]), int.Parse(words[2])); Console.WriteLine("OK"); break;
                case "wait":
                    await Wait(int.Parse(words[1]), int.Parse(words[2])); Console.WriteLine("OK"); break;
                case "end":
                    _end = Stamp.Take(); _measuring = false; _endTransport = CaptureTransport();
                    BenchmarkEvents.Log.MeasurementStop(); CheckFault(); Console.WriteLine("OK"); break;
                case "report":
                    if (_measuring) throw new InvalidOperationException("Reports must be emitted after every node ends measurement.");
                    Console.WriteLine(JsonSerializer.Serialize(Report(_end))); break;
                case "quit": return;
                default: throw new InvalidOperationException("Unknown control command.");
            }
        }
    }
    private void InstrumentRelayPeer()
    {
        // Native RelayHostConnection has no public instrumentation hook. Read its sole
        // admitted peer transport once during setup; use PUBLIC detach/attach to apply
        // the same counting decorator as Direct. No private fields are written, no
        // native protocol is replaced, and no pending workload exists at this boundary.
        if (_relayInstrumented) return;
        var field = typeof(RelayHostConnection).GetField("_peers", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new NotSupportedException("Native sidecar instrumentation shape changed.");
        var peers = ((System.Collections.IEnumerable)field.GetValue(_sidecar)!).Cast<object>().ToArray();
        if (peers.Length != 1) throw new InvalidOperationException("Benchmark requires exactly one admitted Relay client.");
        var pair = peers[0]; var type = pair.GetType();
        uint id = (uint)type.GetProperty("Key")!.GetValue(pair)!;
        var transport = (BITKit.Multiplayer.NetRpc.ITransport)type.GetProperty("Value")!.GetValue(pair)!;
        _runtime!.DetachPeer(id); _counting = new CountingTransport(transport); _runtime.AttachPeer(id, _counting);
        _counters.Add(_counting);
        _relayInstrumented = true;
    }
    private async Task Run(int count, int offset)
    {
        CheckFault();
        _pacer.Reset();
        if (_options.Profile is "idle" or "idle-sync") { await Task.Delay(_options.IdleMilliseconds); return; }
        if (_options.Profile == "control")
        {
            for (int i = 0; i < count; i++) await Pace();
            CheckFault(); return;
        }
        if (_role == "client" && _options.Profile is "void" or "scalar" or "dto" or "bytes")
        {
            if (_options.Profile == "void")
            {
                for (int i = 0; i < count; i++) { var start = Stopwatch.GetTimestamp(); _actor!.Notify(1); Record(i, start); await Pace(); }
                // A separate application-level result fence, NOT a void Return/ACK.
                var expected = offset + count;
                await UntilAsync(async () => await _remote!.Fence() == expected);
                _completed = count; return;
            }
            int next = -1;
            async Task Worker()
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next); if (i >= count) return;
                    long start = Stopwatch.GetTimestamp();
                    int result = _options.Profile switch
                    {
                        "scalar" => await _remote!.Scalar(1000 + i),
                        "dto" => await _remote!.Dto(_dto),
                        "bytes" => await _remote!.Bytes(_payload),
                        _ => throw new InvalidOperationException()
                    };
                    int expected = _options.Profile == "scalar" ? 1000 + i : _options.Profile == "dto" ? 17 + _payload.Length : _payload.Length;
                    if (result != expected) throw new InvalidOperationException("RPC result mismatch.");
                    Record(i, start); Interlocked.Increment(ref _completed); await Pace();
                }
            }
            await Task.WhenAll(Enumerable.Range(0, _options.Concurrency).Select(_ => Worker())); _latencyCount = count;
        }
        else if (_role == "host")
        {
            for (int i = 0; i < count; i++)
            {
                int value = offset + i + 1; long start = Stopwatch.GetTimestamp();
                switch (_options.Profile)
                {
                    case "component": _component.Value = value; await _runtime!.PublishStateAsync(); break;
                    case "syncvar": _local!.ScalarState = value; await _runtime!.PublishStateAsync(); break;
                    case "list": _local!.Items[0] = value; break;
                    case "dictionary": _local!.Counts[0] = value; break;
                    default: throw new InvalidOperationException("Incorrect workload role.");
                }
                Record(i, start); _completed++; await Pace();
            }
            // Final UDP repair is explicitly counted as an extra datagram; it is not a business operation.
            if (_options.Profile == "component") { await Task.Delay(25); await _runtime!.PublishStateAsync(true); }
        }
        CheckFault();
    }
    private void Record(int index, long start) { _latency[index] = Stopwatch.GetTimestamp() - start; _latencyCount = Math.Max(_latencyCount, index + 1); }
    private Task Pace()
    {
        if (_options.Rate > 0) { _pacer.Wait(); return Task.CompletedTask; }
        return _options.PacingMilliseconds == 0 ? Task.CompletedTask : Task.Delay(_options.PacingMilliseconds);
    }
    private async Task Wait(int count, int offset)
    {
        if (_role != "client") return;
        int final = offset + count;
        if (_options.Profile is "component" or "syncvar" or "list" or "dictionary")
        {
            await Until(() => FinalValue() == final && (!_measuring || _options.Profile == "component" || Interlocked.Read(ref _callbacks) >= count));
            await Task.Delay(50); // Drain callbacks before recording totals, included in both process windows.
            if (_measuring && _options.Profile != "component" && _callbacks != count)
                throw new InvalidOperationException($"Expected exactly {count} callbacks, observed {_callbacks}.");
            if (_remote!.Items.Count != _options.ContainerSize || _remote.Counts.Count != _options.ContainerSize)
                throw new InvalidOperationException("Container capacity changed.");
            if (!_monotonic) throw new InvalidOperationException("Non-monotonic revision.");
            _completed = _callbacks;
        }
    }
    private int FinalValue() => _options.Profile switch
    {
        "component" => _component.Value, "syncvar" => _remote?.ScalarState ?? _local!.ScalarState,
        "list" => (_remote ?? _local!).Items[0], "dictionary" => (_remote ?? _local!).Counts[0], _ => 0
    };
    private NodeReport Report(Stamp end)
    {
        long completed = _completed;
        if (_role == "host" && _options.Profile is "void" or "scalar" or "dto" or "bytes")
            completed = (_options.Profile == "void" ? _actor!.Calls : _local!.Calls) - _startCalls;
        var c = _endTransport;
        return new NodeReport(ReportRole, Environment.ProcessId, _options.Profile, Options.HasNoBusinessOperations(_options.Profile) ? 0 : _options.Iterations,
            end.Allocated - _start.Allocated, (end.Ticks - _start.Ticks) / (double)Stopwatch.Frequency,
            end.Gen0 - _start.Gen0, end.Gen1 - _start.Gen1, end.Gen2 - _start.Gen2, completed, _callbacks,
            c?.SentBytes, c?.ReceivedBytes, c?.ReliableSent, c?.FastSent, c?.ComponentFrames,
            _component.Revision, _role == "relay" ? 0 : FinalValue(), _monotonic,
            Statistics.Percentile(_latency, _latencyCount, .50), Statistics.Percentile(_latency, _latencyCount, .95),
            _role == "host" ? "mutation/publish submission (not client delivery)" : _options.Profile == "void" ? "void enqueue (completion fence in elapsed window)" : _options.Profile is "scalar" or "dto" or "bytes" ? "UniTask result round-trip" : "not sampled: use Host publish/submission latency",
            _remote?.GetType().FullName ?? "", Statistics.IsWoven, c?.Returns ?? 0,
            Statistics.Runtime, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, GCSettings.IsServerGC, Stopwatch.Frequency,
            _start, end, Environment.MachineName, typeof(TcpTransport).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? "", _latencyCount, _actor?.Calls ?? 0);
    }
    private TransportTotals? CaptureTransport()
    {
        if (_counters.Count == 0) return null;
        long sent = 0, received = 0, reliable = 0, fast = 0, components = 0; int returns = 0;
        foreach (var counter in _counters)
        {
            sent += Interlocked.Read(ref counter.SentBytes); received += Interlocked.Read(ref counter.ReceivedBytes);
            reliable += Interlocked.Read(ref counter.ReliableSent); fast += Interlocked.Read(ref counter.FastSent);
            components += Interlocked.Read(ref counter.ComponentFrames); returns += Volatile.Read(ref counter.Returns);
        }
        return new TransportTotals(sent, received, reliable, fast, components, returns);
    }
    private void CheckFault() { if (_fault != null) throw new InvalidOperationException("Runtime fault", _fault); }
    private async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        while (!condition()) { CheckFault(); await Task.Delay(1, timeout.Token); }
    }
    private async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        while (!await condition().WaitAsync(timeout.Token)) { CheckFault(); await Task.Delay(1, timeout.Token); }
    }
    public async ValueTask DisposeAsync()
    {
        _services?.Dispose(); _entityServices?.Dispose(); _listener?.Dispose();
        if (_socket != null) await _socket.DisposeAsync();
        await Task.WhenAll(_hostSockets.Select(socket => socket.DisposeAsync().AsTask()));
        if (_sidecar != null) await _sidecar.DisposeAsync();
        if (_relay != null) await _relay.DisposeAsync();
    }
}
