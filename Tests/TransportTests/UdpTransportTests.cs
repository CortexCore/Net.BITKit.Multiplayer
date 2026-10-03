using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TransportTests;

public class UdpTransportTests
{
    private static TransportOptions Options(int max = 1200, int concurrent = 128) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        MaxPacketBytes = max,
        MaxConcurrentSends = concurrent
    };

    private static async Task<(UdpTransport Sender, UdpTransport Receiver)> Pair(int max = 1200, int concurrent = 128)
    {
        var a = new UdpTransport();
        var b = new UdpTransport();
        await a.StartAsync(Options(max, concurrent));
        await b.StartAsync(Options(max, concurrent));
        return (a, b);
    }

    private static async Task End(UdpTransport a, UdpTransport b)
    {
        await a.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await b.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var t in new[] { a, b })
        {
            var stats = t.GetStatistics();
            Assert.Equal(stats.BufferRents, stats.BufferReturns);
            Assert.Equal(0, stats.OutstandingBufferBytes);
            Assert.Equal(0, stats.InFlightSends);
        }
    }

    [Fact]
    public async Task Boundaries_BorrowedPayload_AndOversizeRecovery()
    {
        var (a, b) = await Pair();
        try
        {
            var received = new List<byte[]>();
            var deliveries = new List<RpcDelivery>();
            b.Received += (_, data, mode) => { lock (received) { received.Add(data.ToArray()); deliveries.Add(mode); } };
            byte[] source = Enumerable.Repeat((byte)91, 1200).ToArray();
            await a.SendAsync(b.LocalEndPoint!, source.AsMemory());
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 10, 20 }.AsMemory());
            Assert.Throws<ArgumentOutOfRangeException>(() => a.SendAsync(b.LocalEndPoint!, new byte[1201]).GetAwaiter().GetResult());
            using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                raw.SendTo(new byte[1201], b.LocalEndPoint!);
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 99 });
            await SpinUntil(() => { lock (received) return received.Count == 3; });
            lock (received)
            {
                Assert.Equal(1200, received[0].Length);
                Assert.All(received[0], x => Assert.Equal((byte)91, x));
                Assert.Equal(new byte[] { 10, 20 }, received[1]);
                Assert.Equal(new byte[] { 99 }, received[2]);
                Assert.All(deliveries, x => Assert.Equal(RpcDelivery.Unreliable, x));
            }
            source[0] = 7; // Caller owns the source again after send completion.
            Assert.Equal((byte)91, received[0][0]);
            Assert.Equal(1, a.GetStatistics().RejectedPackets);
            Assert.Equal(1, b.GetStatistics().RejectedPackets);
            Assert.Equal(3, b.GetStatistics().ReceivedPackets);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task LargeDatagramDoesNotTruncateOrPoisonFollowingPackets()
    {
        var (a, b) = await Pair(65507);
        try
        {
            var packets = new List<byte[]>();
            b.Received += (_, data, _) => { lock (packets) packets.Add(data.ToArray()); };
            var large = new byte[65507];
            large[0] = 53;
            large[65506] = 82;
            await a.SendAsync(b.LocalEndPoint!, large);
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 11, 12 });
            await SpinUntil(() => { lock (packets) return packets.Count == 2; });
            lock (packets)
            {
                Assert.Equal(65507, packets[0].Length);
                Assert.Equal(53, packets[0][0]);
                Assert.Equal(82, packets[0][65506]);
                Assert.Equal(new byte[] { 11, 12 }, packets[1]);
            }
            Assert.Equal(1, b.GetStatistics().BufferRents);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task Ipv4PacketLimitRejectsAbove65507WithoutStartingSocket()
    {
        var invalid = new UdpTransport();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalid.StartAsync(Options(65508)));
        await invalid.Completion;
        Assert.Equal(0, invalid.GetStatistics().OutstandingBufferBytes);
    }

    [Fact]
    public async Task DisposeFromReceiveCallbackDoesNotBlockAndStopWaitsForCallback()
    {
        var (a, b) = await Pair();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? stopFromCallback = null;
        try
        {
            b.Received += (_, _, _) => { b.Dispose(); stopFromCallback = b.StopAsync(); entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 1 });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(b.Completion.IsCompleted);
            Assert.Same(b.Completion, stopFromCallback);
            release.Set();
            await b.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, b.GetStatistics().OutstandingBufferBytes);
        }
        finally { release.Set(); await End(a, b); }
    }

    [Fact]
    public async Task StopBeforeStart_CanceledStartup_UnsupportedAndRepeatedStart()
    {
        var before = new UdpTransport();
        await before.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => before.StartAsync(Options()));
        var canceled = new UdpTransport();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.StartAsync(Options(), cts.Token));
        await canceled.Completion;
        Assert.Equal(0, canceled.GetStatistics().OutstandingBufferBytes);
        var active = new UdpTransport();
        await active.StartAsync(Options());
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => active.StartAsync(Options()));
            Assert.Throws<NotSupportedException>(() => active.SendAsync(active.LocalEndPoint!, new byte[1], RpcDelivery.Reliable).GetAwaiter().GetResult());
            Assert.Throws<NotSupportedException>(() => active.SendAsync(new IPEndPoint(IPAddress.IPv6Loopback, 1234), new byte[1]).GetAwaiter().GetResult());
        }
        finally { await active.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => active.StartAsync(Options()));
    }

    [Fact]
    public async Task BindFailureAndCanceledSendLeaveNoBorrowedBuffer()
    {
        var first = new UdpTransport();
        await first.StartAsync(Options());
        var occupied = (IPEndPoint)first.LocalEndPoint!;
        var failed = new UdpTransport();
        try
        {
            await Assert.ThrowsAnyAsync<SocketException>(() => failed.StartAsync(new TransportOptions { LocalEndPoint = occupied }));
            await failed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, failed.GetStatistics().OutstandingBufferBytes);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => first.SendAsync(occupied, new byte[10], cancellationToken: canceled.Token).GetAwaiter().GetResult());
            Assert.Equal(0, first.GetStatistics().InFlightSends);
        }
        finally { await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CallbackExceptionReportedOnceAndReceiveContinues()
    {
        var (a, b) = await Pair();
        try
        {
            int calls = 0, faults = 0;
            b.Received += (_, _, _) => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("client callback"); };
            b.Faulted += _ => Interlocked.Increment(ref faults);
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 1 });
            await SpinUntil(() => Volatile.Read(ref faults) == 1);
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 2 });
            await SpinUntil(() => Volatile.Read(ref calls) == 2);
            Assert.Equal(1, faults);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task SocketSendFailureReleasesSlotAndSubsequentSendWorks()
    {
        var (a, b) = await Pair(1200, 1);
        try
        {
            // Broadcast is disabled on this socket; the OS rejects this actual send.
            await Assert.ThrowsAnyAsync<SocketException>(async () =>
                await a.SendAsync(new IPEndPoint(IPAddress.Broadcast, 54321), new byte[] { 1 }));
            Assert.Equal(0, a.GetStatistics().InFlightSends);
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 2 });
            Assert.Equal(1, a.GetStatistics().SentPackets);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task ClosedPeerIcmpCannotTerminateSharedReceiver()
    {
        var (a, b) = await Pair();
        try
        {
            int received = 0;
            a.Received += (_, payload, _) => { if (payload.Span.SequenceEqual(new byte[] { 42 })) Interlocked.Increment(ref received); };
            IPEndPoint departed;
            using (var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                departed = (IPEndPoint)peer.LocalEndPoint!;
            }
            // Windows loopback produces ICMP Port Unreachable; other OSes may also
            // report it. In either case this must not take down the shared endpoint.
            await a.SendAsync(departed, new byte[] { 1 });
            await Task.Delay(150);
            await b.SendAsync(a.LocalEndPoint!, new byte[] { 42 });
            await SpinUntil(() => Volatile.Read(ref received) == 1);
            Assert.False(a.Completion.IsCompleted);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task StopSettlesAllAcceptedSendTasksAndInterruptsIdleReceive()
    {
        var (a, b) = await Pair(1200, 256);
        try
        {
            var sends = new List<Task>();
            for (int i = 0; i < 200; i++)
            {
                try { sends.Add(a.SendAsync(b.LocalEndPoint!, new byte[1200]).AsTask()); }
                catch (InvalidOperationException) { /* bounded overload, not an accepted send */ }
            }
            await a.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(sends, task => Assert.True(task.IsCompleted));
            Assert.Equal(0, a.GetStatistics().OutstandingBufferBytes);
            await b.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task NonArrayMemoryCopiesOnlyPacketSizeAndReturnsRental()
    {
        var (a, b) = await Pair();
        try
        {
            var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Received += (_, payload, _) => result.TrySetResult(payload.ToArray());
            using var memory = new OpaqueMemory(new byte[] { 3, 4, 5 });
            await a.SendAsync(b.LocalEndPoint!, memory.Memory);
            Assert.Equal(new byte[] { 3, 4, 5 }, await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, a.GetStatistics().BufferRents); // one receive + one packet-size send rental
            Assert.Equal(1, a.GetStatistics().BufferReturns);
        }
        finally { await End(a, b); }
    }

    private sealed class OpaqueMemory : MemoryManager<byte>
    {
        private readonly byte[] bytes;
        public OpaqueMemory(byte[] bytes) => this.bytes = bytes;
        public override Span<byte> GetSpan() => bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Fact]
    public async Task ThrowingMemoryManagerReturnsRentalAndOperationCanBeReused()
    {
        var (a, b) = await Pair(1200, 1);
        try
        {
            long before = a.GetStatistics().OutstandingBufferBytes;
            using var memory = new UnavailableMemory();
            for (int i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await a.SendAsync(b.LocalEndPoint!, memory.Packet));
                Assert.Equal(0, a.GetStatistics().InFlightSends);
                Assert.Equal(before, a.GetStatistics().OutstandingBufferBytes);
            }
            var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Received += (_, bytes, _) => delivered.TrySetResult(bytes.Span.SequenceEqual(new byte[] { 7 }));
            await a.SendAsync(b.LocalEndPoint!, new byte[] { 7 });
            Assert.True(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4, a.GetStatistics().BufferRents); // receive + three failed materializations
            Assert.Equal(3, a.GetStatistics().BufferReturns);
        }
        finally { await End(a, b); }
    }

    private sealed class UnavailableMemory : MemoryManager<byte>
    {
        internal Memory<byte> Packet => CreateMemory(16);
        public override Span<byte> GetSpan() => throw new ObjectDisposedException(nameof(UnavailableMemory));
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Fact]
    public async Task RetainedSourceEndpointsRemainStableAcrossAlternatingSenders()
    {
        var (a, b) = await Pair();
        using var other = new UdpTransport();
        await other.StartAsync(Options());
        try
        {
            var sources = new List<EndPoint>();
            b.Received += (source, _, _) => { lock (sources) sources.Add(source); };
            for (int i = 0; i < 20; i++)
            {
                await (i % 2 == 0 ? a : other).SendAsync(b.LocalEndPoint!, new byte[] { (byte)i });
                await SpinUntil(() => { lock (sources) return sources.Count == i + 1; });
            }
            lock (sources)
                for (int i = 0; i < sources.Count; i++)
                    Assert.Equal(i % 2 == 0 ? a.LocalEndPoint : other.LocalEndPoint, sources[i]);
        }
        finally { await other.StopAsync(); await End(a, b); }
    }

    [Fact]
    public async Task UnobservedFailureAndSuccessKeepTheirOwnResultsAcrossOperationReuse()
    {
        var (a, b) = await Pair(1200, 1);
        try
        {
            var failed = a.SendAsync(new IPEndPoint(IPAddress.Broadcast, 54321), new byte[] { 1 }).AsTask();
            await SpinUntil(() => failed.IsCompleted); // deliberately consume only after reuse
            var firstSuccess = a.SendAsync(b.LocalEndPoint!, new byte[] { 2 }).AsTask();
            await SpinUntil(() => firstSuccess.IsCompleted);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => a.SendAsync(b.LocalEndPoint!, new byte[] { 3 }, cancellationToken: cancellation.Token));
            for (int i = 0; i < 40; i++) await a.SendAsync(b.LocalEndPoint!, new byte[] { 4 });
            await firstSuccess;
            await Assert.ThrowsAnyAsync<SocketException>(() => failed);
            Assert.Equal(41, a.GetStatistics().SentPackets);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public async Task ConcurrentSendsNeverExceedLimitAndIdleShutdownReleasesRent()
    {
        var (a, b) = await Pair(1200, 1);
        try
        {
            var destination = b.LocalEndPoint!;
            var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
            {
                try { await a.SendAsync(destination, new byte[1200]); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            while (tasks.Any(t => !t.IsCompleted))
            {
                Assert.InRange(a.GetStatistics().InFlightSends, 0, 1);
                await Task.Delay(1);
            }
            var results = await Task.WhenAll(tasks);
            Assert.Equal(200, results.Length);
            Assert.Equal(200 - results.Count(x => x), a.GetStatistics().RejectedPackets);
        }
        finally { await End(a, b); }
    }

    [Fact]
    public void FactoryViaRealProviderIsReplaceableAndNeverTracksEndpoint()
    {
        var services = new ServiceCollection();
        services.AddUdpTransport();
        using (var provider = services.BuildServiceProvider())
        {
            var factory = provider.GetRequiredService<ITransportFactory>();
            Assert.IsType<UdpTransportFactory>(factory);
            var first = factory.Create();
            var second = factory.Create();
            Assert.NotSame(first, second);
            provider.Dispose();
            Assert.False(first.Completion.IsCompleted); // Root provider did not capture endpoint.
            first.Dispose();
            second.Dispose();
        }
        var custom = new CustomFactory();
        var replacements = new ServiceCollection();
        replacements.AddSingleton<ITransportFactory>(custom);
        replacements.AddUdpTransport();
        using var otherProvider = replacements.BuildServiceProvider();
        Assert.Same(custom, otherProvider.GetRequiredService<ITransportFactory>());
    }

    private sealed class CustomFactory : ITransportFactory
    {
        public string Name => "test";
        public ITransport Create() => new UdpTransport();
    }

    private static async Task SpinUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(5); }
    }
}
