using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BITKit.Multiplayer.TouchSocket;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class SyncAllocationFactAttribute : FactAttribute
{
    public SyncAllocationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BITKIT_SYNC_BENCH_OUTPUT")))
            Skip = "Set BITKIT_SYNC_BENCH_OUTPUT to run this allocation benchmark alone.";
    }
}
[Collection("Runtime defaults isolation")]
public sealed class SyncAllocationTests
{
    private const int Warm = 128, Iterations = 500;
    private static readonly PeerId H = new("host"), C = new("client");
    private readonly List<Row> rows = new();
    private sealed record Row(string Path, string Operation, int CollectionSize, int EditsPerCall, int Batch,
        int Calls, long AllocatedBytes, double BytesPerCall, double BytesPerEdit, double Milliseconds);
    private sealed class Wire : IRoomWire
    {
        public Wire Other = null!;
        public PeerId Sender;
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft { add { } remove { } }
        public Task SendAsync(PeerId peer, byte[] bytes) { Other.Received?.Invoke(Sender, bytes); return Task.CompletedTask; }
        public void Dispose() { }
    }
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    [SyncAllocationFact]
    public async Task MeasureWovenCollectionPaths()
    {
        await Run("map-set", 1, 1, false);
        await Run("map-set", 64, 1, false);
        await Run("map-set", 256, 1, false);
        await Run("map-batch", 256, 8, false);
        await Run("map-noop", 64, 1, false);
        await Run("list-set", 256, 1, false);
        await Run("set-remove-add", 256, 2, false);
        await Run("dto-set", 64, 1, false);
        await Run("map-set", 256, 1, true);
        await Run("map-batch", 256, 8, true);
        await Run("dto-set", 64, 1, true);
        string output = Environment.GetEnvironmentVariable("BITKIT_SYNC_BENCH_OUTPUT")!;
        Assert.True(Path.IsPathFullyQualified(output));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            WarmupCalls = Warm,
            Counter = "GC.GetTotalAllocatedBytes(true), entire process/both peers; synchronous wire distinguished from real TCP/DMTP",
            Notes = "No forced GC. Input arrays/DTO reused. No logging or event journals. Three batches per path. Snapshot/setup excluded.",
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task Run(string operation, int size, int edits, bool socket)
    {
        IRoomWire hw, cw;
        if (socket)
        {
            var hostWire = new TouchSocketHostWire(); var clientWire = new TouchSocketClientWire(H);
            hw = hostWire; cw = clientWire;
            int port = FreePort();
            await hostWire.StartAsync(port, "sync-perf");
            await clientWire.ConnectAsync("127.0.0.1", port, "sync-perf"); hostWire.Admit(C, clientWire.SessionId);
        }
        else
        {
            var a = new Wire { Sender = H }; var b = new Wire { Sender = C }; a.Other = b; b.Other = a; hw = a; cw = b;
        }
        using var host = new RpcRuntime(NetworkRole.Host, "sync-perf", H, H, hw);
        using var client = new RpcRuntime(NetworkRole.Client, "sync-perf", C, H, cw);
        int errors = 0; host.UnhandledDispatch += _ => Interlocked.Increment(ref errors); client.UnhandledDispatch += _ => Interlocked.Increment(ref errors);
        host.RegisterMember(new RoomMember(C)); client.ConfirmReady();
        await Until(() => client.IsReady);
        var type = RuntimeTests.WovenAssembly.GetType("Fixture.SyncPerfState")!;
        var source = Activator.CreateInstance(type)!; var replica = Activator.CreateInstance(type)!;
        var map = Get<SyncDictionary<int, int>>(source, "Map"); var remoteMap = Get<SyncDictionary<int, int>>(replica, "Map");
        var list = Get<SyncList<int>>(source, "List"); var remoteList = Get<SyncList<int>>(replica, "List");
        var set = Get<SyncHashSet<int>>(source, "Set"); var remoteSet = Get<SyncHashSet<int>>(replica, "Set");
        var dtos = Get<SyncDictionary<int, Fixture.SyncItem>>(source, "Dtos"); var remoteDtos = Get<SyncDictionary<int, Fixture.SyncItem>>(replica, "Dtos");
        for (int i = 0; i < size; i++)
        {
            if (operation.StartsWith("map")) map[i] = 0;
            else if (operation == "list-set") list.Add(0);
            else if (operation == "set-remove-add") set.Add(i);
            else dtos[i] = new Fixture.SyncItem { Count = 0, Name = "item" };
        }
        var key = new TargetKey("sync-perf");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Synchronized += _ => ready.TrySetResult(true);
        host.Bind(key, source, owner: C); client.Bind(key, replica);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var signal = new ManualResetEventSlim();
        long expectedRevision = 0;
        remoteMap.Changed += (in SyncDictionaryChange<int, int> c) =>
        { if (c.Version == Volatile.Read(ref expectedRevision) && (operation != "map-batch" || c.Key == edits - 1)) signal.Set(); };
        remoteList.Changed += (in SyncListChange<int> c) => { if (c.Version == Volatile.Read(ref expectedRevision)) signal.Set(); };
        remoteSet.Changed += (in SyncHashSetChange<int> c) => { if (c.Version == Volatile.Read(ref expectedRevision)) signal.Set(); };
        remoteDtos.Changed += (in SyncDictionaryChange<int, Fixture.SyncItem> c) => { if (c.Version == Volatile.Read(ref expectedRevision)) signal.Set(); };
        var commands = new SyncDictionary<int, int>.Edit[edits];
        var dto = new Fixture.SyncItem { Name = "item" };
        int serial = 0;
        SyncCollection target = operation.StartsWith("map") ? map : operation == "list-set" ? list : operation == "set-remove-add" ? set : dtos;
        Action update = () =>
        {
            int value = ++serial;
            if (operation == "map-noop") { map[0] = 0; return; }
            signal.Reset();
            Volatile.Write(ref expectedRevision, target.Version + (operation == "set-remove-add" ? 2 : 1));
            if (operation == "map-set") map[0] = value;
            else if (operation == "map-batch")
            {
                for (int i = 0; i < edits; i++) commands[i] = SyncDictionary<int, int>.Edit.Set(i, value);
                map.ApplyBatch(commands);
            }
            else if (operation == "list-set") list[0] = value;
            else if (operation == "set-remove-add") { set.Remove(0); set.Add(0); }
            else { dto.Count = value; dtos[0] = dto; }
            if (!signal.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Collection benchmark update did not apply");
        };
        for (int i = 0; i < Warm; i++) update();
        for (int batch = 0; batch < 3; batch++)
        {
            long time = Stopwatch.GetTimestamp(), before = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < Iterations; i++) update();
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            long elapsed = Stopwatch.GetTimestamp() - time;
            rows.Add(new Row(socket ? "tcp-dmtp" : "synchronous-wire", operation, size, edits, batch,
                Iterations, allocated, allocated / (double)Iterations, allocated / (double)(Iterations * edits), elapsed * 1000.0 / Stopwatch.Frequency));
        }
        Assert.Equal(0, errors);
        long expected = (Warm + Iterations * 3L) * (operation == "map-noop" ? 0 : operation == "set-remove-add" ? 2 : 1);
        Assert.Equal(expected, target.Version);
        if (operation.StartsWith("map")) { Assert.Equal(expected, remoteMap.Version); Assert.Equal(operation == "map-noop" ? 0 : serial, remoteMap[0]); }
        else if (operation == "list-set") { Assert.Equal(expected, remoteList.Version); Assert.Equal(serial, remoteList[0]); }
        else if (operation == "set-remove-add") { Assert.Equal(expected, remoteSet.Version); Assert.Equal(size, remoteSet.Count); }
        else { Assert.Equal(expected, remoteDtos.Version); Assert.Equal(serial, remoteDtos[0].Count); }
    }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static int FreePort()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port)); return port;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { }
            finally { probe.Stop(); }
        }
        throw new IOException("No shared TCP/UDP port available");
    }
}
