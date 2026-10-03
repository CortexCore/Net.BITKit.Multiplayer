using System.Buffers.Binary;
using Cysharp.Threading.Tasks;
using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer.LiteNetLibDirect;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.Samples.NetRpcGodot;
using Xunit;

namespace BITKit.Multiplayer.LiteNetLibTests;

public class DirectTests
{
    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(2, timeout.Token);
    }
    private static int EarlyCount(LiteNetLibTransport transport)
    { lock (Field(transport, "_gate")) return ((Queue<byte[]>)Field(transport, "_early")).Count; }
    private static void Quiescent(LiteNetLibEndpoint endpoint)
    {
        var manager = Field(endpoint, "_manager");
        Assert.False((bool)manager.GetType().GetProperty("IsRunning")!.GetValue(manager)!);
        Assert.Equal(0, (int)Field(endpoint, "_peers").GetType().GetProperty("Count")!.GetValue(Field(endpoint, "_peers"))!);
        Assert.True(endpoint.LocalPort > 0);
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, endpoint.LocalPort));
    }
    private static async Task<(LiteNetLibEndpoint Server, LiteNetLibEndpoint Client, LiteNetLibTransport Host, LiteNetLibTransport Remote)> Pair()
    {
        var server = LiteNetLibEndpoint.Listen(0);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var accept = server.AcceptAsync(timeout.Token);
            var (client, remote) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", server.LocalPort, timeout.Token);
            return (server, client, await accept, remote);
        }
        catch { await server.DisposeAsync(); throw; }
    }
    [Fact]
    public async Task Reliable_ordered_and_send_ends_payload_loan()
    {
        var p = await Pair();
        await using var server = p.Server; await using var client = p.Client;
        var values = new List<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Host.OnReceived += bytes => { lock (values) { values.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.Span)); if (values.Count == 100) completed.TrySetResult(); } };
        var borrowed = new byte[4];
        for (int i = 0; i < 100; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(borrowed, i);
            await p.Remote.Send(borrowed);
            Array.Fill(borrowed, (byte)255); // library must have copied before completion
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(Enumerable.Range(0, 100), values);
    }
    [Fact]
    public async Task Maximum_reliable_frame_fragments_and_unreliable_oversize_is_rejected_without_fallback()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        var complete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Host.OnReceived += data => complete.TrySetResult(data.Span[0] == 0 && data.Span[25] == 0 && data.Span[data.Length - 1] == 0 ? data.Length : -1);
        byte[] frame = new byte[NetRpcCodec.HeaderBytes + NetRpcCodec.MaxPayloadBytes];
        await p.Remote.Send(frame);
        Array.Fill(frame, (byte)0xFF);
        Assert.Equal(frame.Length, await complete.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.InRange(p.Remote.UnreliablePayloadLimit, 500, 1600);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await p.Remote.SendFast(new byte[p.Remote.UnreliablePayloadLimit + 1]));
        var fast = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Remote.OnReceived += data => fast.TrySetResult(data.Span[0]);
        await p.Host.SendFast(new byte[] { 37 });
        Assert.Equal((byte)37, await fast.Task.WaitAsync(TimeSpan.FromSeconds(8)));
    }
    [Fact]
    public async Task Cancellation_shutdown_reconnect_and_old_callbacks_do_not_reach_new_connection()
    {
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", 1234, canceled.Token));
        }
        var server = LiteNetLibEndpoint.Listen(0); await using (server)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var firstAccept = server.AcceptAsync(deadline.Token);
            var (firstClient, first) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", server.LocalPort, deadline.Token);
            var firstHost = await firstAccept;
            int oldClosed = 0, stale = 0;
            firstHost.Closed += () => Interlocked.Increment(ref oldClosed);
            firstHost.OnReceived += _ => Interlocked.Increment(ref stale);
            await firstClient.DisposeAsync();
            await Task.Delay(100);
            Assert.Equal(1, oldClosed);
            var nextAccept = server.AcceptAsync(deadline.Token);
            var (nextClient, next) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", server.LocalPort, deadline.Token);
            await using (nextClient)
            {
                var nextHost = await nextAccept;
                Assert.NotSame(firstHost, nextHost);
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                nextHost.OnReceived += _ => received.TrySetResult();
                await next.Send(new byte[] { 7 });
                await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(0, stale);
                Assert.Equal(1, oldClosed);
            }
        }
    }
    [Fact]
    public async Task Two_rooms_never_cross_deliver_and_dispose_stops_poll()
    {
        var a = await Pair(); var b = await Pair();
        await using var aServer = a.Server; await using var aClient = a.Client;
        await using var bServer = b.Server; await using var bClient = b.Client;
        var gotA = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotB = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Host.OnReceived += data => gotA.TrySetResult(data.Span[0]);
        b.Host.OnReceived += data => gotB.TrySetResult(data.Span[0]);
        await Task.WhenAll(a.Remote.Send(new byte[] { 11 }).AsTask(), b.Remote.Send(new byte[] { 22 }).AsTask());
        Assert.Equal((byte)11, await gotA.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal((byte)22, await gotB.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        await a.Client.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await a.Remote.Send(new byte[] { 1 }));
    }
    [Fact]
    public async Task Cancel_pending_connect_and_accept()
    {
        await using var server = LiteNetLibEndpoint.Listen(0);
        using var cancelAccept = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.AcceptAsync(cancelAccept.Token));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", 1, canceled.Token));
        var waiting = server.AcceptAsync();
        await server.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }
    [Fact]
    public async Task Concurrent_sends_own_data_and_closure_is_once()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        var received = new HashSet<int>(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Host.OnReceived += data => { lock (received) { received.Add(BinaryPrimitives.ReadInt32LittleEndian(data.Span)); if (received.Count == 80) done.TrySetResult(); } };
        await Task.WhenAll(Enumerable.Range(0, 80).Select(i => Task.Run(async () =>
        {
            var data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, i);
            await p.Remote.Send(data); Array.Fill(data, (byte)0xff);
        })));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(80, received.Count);
        int closed = 0; p.Remote.Closed += () => Interlocked.Increment(ref closed);
        await p.Remote.DisposeAsync(); await p.Remote.DisposeAsync(); await client.DisposeAsync();
        Assert.Equal(1, closed);
    }
    [Fact]
    public async Task Early_peer_frames_wait_for_runtime_subscription()
    {
        await using var server = LiteNetLibEndpoint.Listen(0);
        var (client, transport) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", server.LocalPort);
        await using (client)
        {
            await transport.Send(new byte[] { 42 });
            await Task.Delay(70); // deliberately hold off Accept/Attach while PollEvents delivers
            var host = await server.AcceptAsync();
            var received = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnReceived += memory => received.TrySetResult(memory.Span[0]);
            Assert.Equal((byte)42, await received.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        }
    }
    [Fact]
    public async Task Early_drain_has_only_poll_owner_and_callback_can_dispose_without_lock_inversion()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        await p.Remote.Send(new byte[] { 1 }); await p.Remote.Send(new byte[] { 2 });
        await Until(() => EarlyCount(p.Host) == 2);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<byte>();
        UniTask sent;
        // Hold the poll lock: subscription must not execute callbacks on this thread.
        lock (Field(server, "_pollGate"))
        {
            p.Host.OnReceived += data =>
            {
                Assert.True(Monitor.IsEntered(Field(server, "_pollGate")));
                Assert.False(Monitor.IsEntered(Field(p.Host, "_gate")));
                values.Add(data.Span[0]);
                if (values.Count == 3)
                {
                    server.DisposeAsync().GetAwaiter().GetResult();
                    done.TrySetResult();
                }
            };
            Assert.Empty(values);
            sent = p.Remote.Send(new byte[] { 3 });
            Assert.Equal(UniTaskStatus.Succeeded, sent.Status);
        }
        await sent;
        await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await server.DisposeAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, values);
        Quiescent(server);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_before_attach_notifies_late_runtime_and_wrapper_subscribers(bool wrapped)
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        await p.Host.DisposeAsync();
        using var wrapper = wrapped ? new ImpairedTransport(p.Host, p.Host, new NetworkImpairment(), new ConcurrentQueue<Exception>()) : null;
        ITransportLifetime lifetime = wrapper == null ? p.Host : wrapper;
        int notified = 0;
        lifetime.Closed += () => Interlocked.Increment(ref notified);
        await p.Host.DisposeAsync();
        Assert.Equal(1, notified);
        var runtime = new RpcContextService(new EmptyServices(), true);
        int detached = 0; runtime.PeerDisconnected += _ => detached++;
        runtime.AttachPeer(2, wrapper == null ? p.Host : wrapper);
        Assert.Equal(1, detached);
        runtime.Dispose();
    }
    [Fact]
    public async Task Closed_add_racing_end_is_delivered_once_per_subscription()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        var counts = new int[64];
        using var start = new ManualResetEventSlim();
        var additions = Enumerable.Range(0, counts.Length).Select(i => Task.Run(() => { start.Wait(); p.Host.Closed += () => Interlocked.Increment(ref counts[i]); })).ToArray();
        var close = Task.Run(async () => { start.Wait(); await p.Host.DisposeAsync(); });
        start.Set(); await Task.WhenAll(additions.Append(close));
        Assert.All(counts, count => Assert.Equal(1, count));
    }
    [Fact]
    public async Task Actual_wrapper_preserves_pre_subscription_frames_in_order()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        // Valid opaque frames: the sample's impairment decoder inspects kind only.
        byte[] Frame(uint sequence) => NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Call, 1, sequence, 0, 0, ReadOnlyMemory<byte>.Empty));
        await p.Remote.Send(Frame(1)); await p.Remote.Send(Frame(2));
        await Until(() => EarlyCount(p.Host) == 2);
        using var wrapper = new ImpairedTransport(p.Host, p.Host, new NetworkImpairment(), new ConcurrentQueue<Exception>());
        Assert.Equal(2, EarlyCount(p.Host));
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<uint>();
        wrapper.OnReceived += data => { values.Add(NetRpcCodec.Decode(data).MethodId); if (values.Count == 3) got.TrySetResult(); };
        await p.Remote.Send(Frame(3));
        await got.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(new uint[] { 1, 2, 3 }, values);
    }
    [Fact]
    public async Task Throwing_receive_and_close_callbacks_shutdown_socket_and_every_peer()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        var secondAccept = server.AcceptAsync();
        var (secondClient, _) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", server.LocalPort);
        await using var secondEndpoint = secondClient;
        var second = await secondAccept;
        int closed = 0;
        p.Host.Closed += () => throw new InvalidOperationException("close subscriber failure");
        p.Host.Closed += () => Interlocked.Increment(ref closed);
        second.Closed += () => Interlocked.Increment(ref closed);
        p.Host.OnReceived += _ => throw new InvalidOperationException("receive subscriber failure");
        await p.Remote.Send(new byte[] { 9 });
        await Until(() => ((UniTask)Field(server, "_poll")).Status != UniTaskStatus.Pending);
        Assert.Equal(2, closed);
        Quiescent(server);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.Send(new byte[] { 1 }));
        await server.DisposeAsync();
    }
    [Fact]
    public async Task Throwing_close_during_explicit_dispose_does_not_strand_endpoint()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        p.Host.Closed += () => throw new InvalidOperationException("close subscriber failure");
        await server.DisposeAsync();
        Quiescent(server);
        Assert.NotEqual(UniTaskStatus.Pending, ((UniTask)Field(server, "_poll")).Status);
    }
    [Fact]
    public async Task Close_callback_can_reenter_endpoint_disposal_from_transport_disposal()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        p.Host.Closed += () => server.DisposeAsync().GetAwaiter().GetResult();
        await Task.Run(async () => await p.Host.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(8));
        await server.DisposeAsync();
        Quiescent(server);
    }
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
