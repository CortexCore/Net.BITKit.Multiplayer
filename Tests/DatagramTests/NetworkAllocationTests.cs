using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;
using Xunit;

namespace DatagramTests;

// Opt-in: run alone in a fresh test process. Process-wide bytes include both socket ends,
// scheduling and this unchanged harness; they are not per-thread or sampled allocations.
public sealed class NetworkAllocationFactAttribute : FactAttribute
{
    public NetworkAllocationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BITKIT_NETWORK_BENCH_OUTPUT")))
            Skip = "Set BITKIT_NETWORK_BENCH_OUTPUT to an absolute JSON output path to run the network benchmark.";
    }
}

public sealed class NetworkAllocationTests
{
    private const int Count = 3000;
    private readonly byte[] payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    private readonly List<Sample> samples = new();
    private sealed record Sample(string Path, int Iteration, int Packets, long AllocatedBytes,
        double BytesPerDeliveredPayload, double ElapsedMilliseconds, int Gen0, int Gen1, int Gen2);

    [NetworkAllocationFact]
    public async Task MeasureRealSocketPaths()
    {
        await Native();
        await Authenticated("");
        await Authenticated("peer-目标");
        await Relay();
        string path = Environment.GetEnvironmentVariable("BITKIT_NETWORK_BENCH_OUTPUT")!;
        Assert.True(Path.IsPathFullyQualified(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            PayloadBytes = payload.Length,
            WarmupPacketsPerPath = 512,
            Counter = "GC.GetTotalAllocatedBytes(true); both endpoints and unchanged harness; no forced GC",
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Measure(string name, Action send, Receipt receipt)
    {
        for (int i = 0; i < 512; i++) Exchange(send, receipt);
        for (int run = 0; run < 3; run++)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long startTime = Stopwatch.GetTimestamp();
            long startBytes = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < Count; i++) Exchange(send, receipt);
            long bytes = GC.GetTotalAllocatedBytes(true) - startBytes;
            long elapsed = Stopwatch.GetTimestamp() - startTime;
            samples.Add(new Sample(name, run, Count, bytes, bytes / (double)Count,
                elapsed * 1000.0 / Stopwatch.Frequency,
                GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2));
        }
        Assert.Equal(0, receipt.Corrupt);
        Assert.Equal(512 + Count * 3, receipt.Count);
    }

    private static void Exchange(Action send, Receipt receipt)
    {
        receipt.Signal.Reset();
        send();
        if (!receipt.Signal.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Benchmark packet not delivered; do not report dropped traffic as an optimization.");
    }

    private sealed class Receipt : IDisposable
    {
        internal readonly ManualResetEventSlim Signal = new();
        private readonly byte[] expected;
        internal int Count, Corrupt;
        internal Receipt(byte[] expected) => this.expected = expected;
        internal void Receive(ReadOnlyMemory<byte> bytes, bool identityValid = true)
        {
            if (!identityValid || !bytes.Span.SequenceEqual(expected)) Interlocked.Increment(ref Corrupt);
            Interlocked.Increment(ref Count);
            Signal.Set();
        }
        public void Dispose() => Signal.Dispose();
    }

    private static void Complete(ValueTask operation)
    {
        if (operation.IsCompleted) operation.GetAwaiter().GetResult();
        else operation.AsTask().GetAwaiter().GetResult();
    }

    private async Task Native()
    {
        using var receipt = new Receipt(payload);
        using var server = new UdpTransport();
        using var client = new UdpTransport();
        server.Received += (_, bytes, delivery) => receipt.Receive(bytes, delivery == RpcDelivery.Unreliable);
        await server.StartAsync(new TransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        await client.StartAsync(new TransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        var destination = server.LocalEndPoint!;
        try { Measure("native-udp", () => Complete(client.SendAsync(destination, payload)), receipt); }
        finally { await client.StopAsync(); await server.StopAsync(); }
        Assert.Equal(0, client.GetStatistics().OutstandingBufferBytes);
        Assert.Equal(0, server.GetStatistics().OutstandingBufferBytes);
    }

    private sealed class CapturingFactory : ITransportFactory
    {
        internal ITransport Last = null!;
        public string Name => "benchmark-native";
        public ITransport Create() => Last = new UdpTransport();
    }

    private async Task Authenticated(string claim)
    {
        using var receipt = new Receipt(payload);
        var peer = new PeerId("peer");
        var factory = new CapturingFactory();
        using var server = new UdpLane(true, (sender, bytes, name) => receipt.Receive(bytes, sender.Equals(peer) && name == claim), factory: factory);
        using var client = new UdpLane(false, (_, _, _) => { });
        await server.Start(0, IPAddress.Loopback);
        await client.Start(0, IPAddress.Loopback, (IPEndPoint)factory.Last.LocalEndPoint!);
        var credential = UdpLane.NewCredential();
        var serverGrant = server.Install(peer, credential);
        await client.Wait(client.Install(peer, credential), CancellationToken.None);
        await server.Wait(serverGrant, CancellationToken.None);
        try { Measure(claim.Length == 0 ? "authenticated-empty-claim" : "authenticated-utf8-claim",
            () => client.Send(peer, payload, claim).GetAwaiter().GetResult(), receipt); }
        finally
        {
            client.Dispose(); server.Dispose();
            await Task.WhenAll(client.TransportCompletion, server.TransportCompletion);
        }
        Assert.Equal(client.PoolRents, client.PoolReturns);
        Assert.Equal(server.PoolRents, server.PoolReturns);
    }

    private sealed class Authorizer : IRelayRoomAuthorizer
    {
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token) =>
            Task.FromResult<RelayRoomIdentity?>(new RelayRoomIdentity { Scope = "bench-scope", HostPeerId = new PeerId("host") });
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token) => Task.FromResult(true);
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token) => Task.CompletedTask;
    }

    private async Task Relay()
    {
        // Existing Relay API requires a concrete shared TCP/UDP port.
        int port = UdpSocketTests.Port();
        using var receipt = new Receipt(payload);
        using var reliableReceipt = new Receipt(payload);
        using var server = new TouchSocketRelayServer(new Authorizer());
        using var host = new RelayHostWire();
        var hostId = new PeerId("host"); var peer = new PeerId("alice");
        using var client = new RelayClientWire(hostId);
        var options = new RelayConnectOptions { Port = port, RoomId = "bench-room", Scope = "bench-scope", HostPeerId = hostId, HostCredential = "bench" };
        await server.StartAsync(new RelayListenOptions { Port = port });
        await host.ConnectAsync(options, _ => Task.FromResult((peer, "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(options);
        Assert.Equal("ok", await client.RequestAdmissionAsync("bench"));
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!host.IsUnreliableReady(peer) || !client.IsUnreliableReady(hostId))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Relay UDP binding failed.");
            await Task.Delay(10);
        }
        host.UnreliableReceived += (sender, bytes) => receipt.Receive(bytes, sender.Equals(peer));
        host.MemoryReceived += (sender, bytes) => reliableReceipt.Receive(bytes, sender.Equals(peer));
        Measure("relay-udp-two-hops", () => client.SendUnreliableAsync(hostId, payload).GetAwaiter().GetResult(), receipt);
        Measure("relay-reliable-memory", () => Complete(client.SendMemoryAsync(hostId, payload)), reliableReceipt);
    }
}
