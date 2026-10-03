using Cysharp.Threading.Tasks;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcHotspotTests
{
    private sealed class ReplyTransport : NetTransport
    {
        private readonly byte[] _reply;
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public int Sends;
        public ReplyTransport()
        {
            using var bag = NetMessageBag.Pool(); bag.Write(42);
            _reply = NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Return, 7, 8, 0, bag.Count, bag.Memory));
        }
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Sends++;
            BinaryPrimitives.WriteUInt32LittleEndian(_reply.AsSpan(9), NetRpcCodec.Decode(payload).RequestId);
            OnReceived?.Invoke(_reply); return default;
        }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Send(payload, cancellationToken);
    }
    private sealed class HeldTransport : NetTransport, ITransportLifetime
    {
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public event Action? Closed;
        public readonly List<ReadOnlyMemory<byte>> Loans = new();
        public readonly List<TaskCompletionSource<bool>> Sends = new();
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Loans.Add(payload);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Sends.Add(completion); return completion.Task.AsUniTask(false);
        }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Send(payload, cancellationToken);
        public void Reply(int send, int result)
        {
            var request = NetRpcCodec.Decode(Loans[send]);
            using var bag = NetMessageBag.Pool(); bag.Write(result);
            OnReceived?.Invoke(NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Return, request.TargetId, request.MethodId, request.RequestId, bag.Count, bag.Memory)));
        }
        public Action? CaptureClosed() => Closed;
    }
    private static NetRpcModel Call => new(NetRpcMessageKind.Call, 7, 8, 0, 0, ReadOnlyMemory<byte>.Empty);

    [Fact]
    public async Task WarmSynchronousUniTaskRequestsDoNotAllocateWaitersOrCopyResultBuffers()
    {
        var transport = new ReplyTransport(); using var provider = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(provider, transport, false);
        var context = runtime.CreateContext(7);
        for (int i = 0; i < 1000; i++) Assert.Equal(42, await context.Request<int>(Call));
        long before = GC.GetAllocatedBytesForCurrentThread(); int sum = 0;
        for (int i = 0; i < 1000; i++) sum += await context.Request<int>(Call);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(42000, sum); Assert.Equal(0, bytes);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotSendAndPreservesCancellationToken()
    {
        var transport = new ReplyTransport(); using var provider = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(provider, transport, false);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CreateContext(7).RequestTask<int>(Call, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public async Task TimedOutSendKeepsItsBufferAndLateSendFailureCannotFailReusedWaiter()
    {
        var transport = new HeldTransport(); using var provider = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(provider, transport, false) { RequestTimeout = TimeSpan.FromMilliseconds(20) };
        var context = runtime.CreateContext(7);
        Task<int> first;
        using (var bag = NetMessageBag.Pool()) { bag.Write(123); first = context.RequestTask<int>(new NetRpcModel(Call.Kind, 7, 8, 0, bag.Count, bag.Memory)); }
        Assert.Equal(RpcError.Timeout, (await Assert.ThrowsAsync<RpcException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)))).Error);
        // Force writer/frame pools to be used while the original transport still holds its loan.
        for (int i = 0; i < 100; i++) { using var bag = NetMessageBag.Pool(); bag.Write(i); }
        var original = NetRpcCodec.Decode(transport.Loans[0]);
        using (var reader = new NetMessageReader(original.Payload, original.ArgumentCount)) Assert.Equal(123, reader.Read<int>());
        runtime.RequestTimeout = TimeSpan.FromSeconds(5); var second = context.RequestTask<int>(Call);
        transport.Sends[0].SetException(new IOException("late send failure"));
        transport.Reply(0, 999); Assert.False(second.IsCompleted);
        transport.Reply(1, 42); Assert.Equal(42, await second);
        transport.Sends[1].SetResult(true);
    }

    [Fact]
    public async Task CancellationWhileSendIsHeldDoesNotReleaseTheLoanOrCancelNextRequest()
    {
        var transport = new HeldTransport(); using var provider = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(provider, transport, false);
        var context = runtime.CreateContext(7); using var cancellation = new CancellationTokenSource();
        var first = context.RequestTask<int>(Call, cancellation.Token); cancellation.Cancel();
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first)).CancellationToken);
        var second = context.Request<int>(Call).AsTask();
        Assert.NotEqual(NetRpcCodec.Decode(transport.Loans[0]).RequestId, NetRpcCodec.Decode(transport.Loans[1]).RequestId);
        transport.Reply(0, 999); transport.Reply(1, 42); Assert.Equal(42, await second);
        transport.Sends[0].SetResult(true); transport.Sends[1].SetResult(true);
    }

    [Fact]
    public async Task ConcurrentGeneratedCallsKeepResultsFaultsAndVoidCompletionsSeparate()
    {
        var (a, b) = NetRpcDesignTests.Pair.Create(); using var host = NetRpcDesignTests.Host(b); using var client = NetRpcDesignTests.Client(a);
        _ = host.GetRequiredService<RpcContextService>(); var proxy = client.GetRequiredService<NetRpcDesignTests.IGame>();
        for (int batch = 0; batch < 4; batch++)
        {
            var calls = Enumerable.Range(0, 128).Select(i => proxy.DelayValue(i + batch * 128, 5 + i % 5)).ToArray();
            Assert.Equal(Enumerable.Range(batch * 128, 128), await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => proxy.Delay(5)));
            await Assert.ThrowsAsync<RpcException>(() => proxy.Fail());
            Assert.Equal(42, await proxy.Value(42)); Assert.Equal(42, await proxy.UniPlus(20, 22)); await proxy.UniWait();
        }
    }

    public interface IScalar { int Value { get; } }
    private sealed class Scalar : IScalar { public int Value => 42; }

    [Fact]
    public async Task DensePeerSnapshotDoesNotRedirectAnInFlightBroadcastToReplacementConnection()
    {
        using var provider = new ServiceCollection().BuildServiceProvider(); using var host = new RpcContextService(provider, true);
        var blocker = new HeldTransport(); var old = new ReplyTransport(); var replacement = new ReplyTransport();
        host.AttachPeer(2, blocker); host.AttachPeer(3, old); host.RegisterTarget(7, new Scalar(), typeof(IScalar));
        var publish = host.PublishStateAsync(); Assert.Single(blocker.Sends);
        host.DetachPeer(3); host.AttachPeer(3, replacement); blocker.Sends[0].SetResult(true);
        await publish.AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, old.Sends); Assert.Equal(0, replacement.Sends);
        await host.PublishStateAsync(); Assert.Equal(0, replacement.Sends); // unchanged value stays skipped
    }

    [Fact]
    public void DelayedClosedCallbackCannotDetachReplacementWithTheSamePeerId()
    {
        using var provider = new ServiceCollection().BuildServiceProvider(); using var host = new RpcContextService(provider, true);
        var old = new HeldTransport(); host.AttachPeer(2, old); var delayedClosed = old.CaptureClosed();
        int disconnects = 0; host.PeerDisconnected += _ => disconnects++;
        host.DetachPeer(2); host.AttachPeer(2, new ReplyTransport()); delayedClosed!();
        Assert.Equal(1, disconnects); host.DetachPeer(2); Assert.Equal(2, disconnects);
    }

    [Fact]
    public async Task InlineTcpReadsHandleFragmentedHeadersPayloadsEmptyFramesAndBackToBackFrames()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var accept = listener.AcceptAsync();
        await using var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port); await using var server = await accept;
        var frames = new List<byte[]>(); var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnReceived += data => { frames.Add(data.ToArray()); if (frames.Count == 3) received.TrySetResult(true); };
        var tcp = (TcpClient)typeof(TcpTransport).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        var stream = tcp.GetStream(); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 3);
        foreach (byte value in header) { await stream.WriteAsync(new[] { value }); await Task.Delay(2); }
        await stream.WriteAsync(new byte[] { 1 }); await Task.Delay(2); await stream.WriteAsync(new byte[] { 2, 3 });
        await stream.WriteAsync(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 99 });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0]); Assert.Empty(frames[1]); Assert.Equal(new byte[] { 99 }, frames[2]);
    }
}
