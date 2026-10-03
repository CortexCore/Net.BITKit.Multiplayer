using System.Reflection;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.RemoteCompiler;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcUniTaskTests
{
    public interface IAsyncApi
    {
        UniTask<int> Echo(int value);
        UniTask<int> Delayed(int value);
        UniTask Completed();
        UniTask<int> Fail();
        UniTask<uint> CapturedSender();
        Task<uint> CompatibilitySender();
    }
    public sealed class AsyncApi : IAsyncApi
    {
        public UniTask<int> Echo(int value) => UniTask.FromResult(value);
        public async UniTask<int> Delayed(int value) { await Task.Delay(5).ConfigureAwait(false); return value; }
        public UniTask Completed() => UniTask.CompletedTask;
        public async UniTask<int> Fail() { await Task.Delay(5).ConfigureAwait(false); throw new InvalidOperationException("async UniTask fault"); }
        public async UniTask<uint> CapturedSender()
        {
            var call = NetRpcCallContext.Current ?? throw new InvalidOperationException("Missing invocation context.");
            await Task.Delay(5).ConfigureAwait(false); return call.SenderPeerId;
        }
        public async Task<uint> CompatibilitySender()
        { await Task.Delay(5).ConfigureAwait(false); return NetRpcCallContext.Current!.SenderPeerId; }
    }
    private static (ServiceProvider Host, ServiceProvider Client) Providers()
    {
        var (a, b) = NetRpcDesignTests.Pair.Create();
        var host = new ServiceCollection().AddGeneratedRemoteInterfaces().AddNetRpcService<IAsyncApi, AsyncApi>().AddNetRpc(true, _ => b).BuildServiceProvider();
        var client = new ServiceCollection().AddGeneratedRemoteInterfaces().AddRemoteInterface<IAsyncApi>().AddNetRpc(false, _ => a).BuildServiceProvider();
        _ = host.GetRequiredService<RpcContextService>(); return (host, client);
    }

    [Fact]
    public void DefaultRuntimeAndGeneratedReceiversHaveNoTaskBridge()
    {
        Assert.Equal(typeof(UniTask<NetMessageBag>), typeof(NetRpcReceiver).GetMethod("Invoke")!.ReturnType);
        Assert.Equal(typeof(UniTask), typeof(NetTransport).GetMethod("Send")!.ReturnType);
        Assert.Equal(typeof(UniTask), typeof(RpcContextService).GetMethod("PublishStateAsync")!.ReturnType);
        Assert.Equal(typeof(UniTask), typeof(RpcContext).GetMethods().Single(m => m.Name == "Request" && !m.IsGenericMethod).ReturnType);
        var source = RemoteInterfaceSourceGenerator.Generate<IAsyncApi>();
        Assert.Contains("return _context.Request<global::System.Int32>(model)", source);
        Assert.Contains("return NetRpcResults.CompleteUniTask", source);
        Assert.DoesNotContain(".AsTask()", source);
        Assert.DoesNotContain("async global::Cysharp.Threading.Tasks.UniTask", source);
    }

    [Fact]
    public async Task SuspendedUniTaskReceiversCompleteFaultAndRestoreInvocationPrefixContext()
    {
        var pair = Providers(); using var host = pair.Host; using var client = pair.Client;
        var proxy = client.GetRequiredService<IAsyncApi>();
        Assert.Null(NetRpcCallContext.Current);
        var delayed = proxy.Delayed(42); Assert.Equal(UniTaskStatus.Pending, delayed.Status);
        Assert.Null(NetRpcCallContext.Current); Assert.Equal(42, await delayed);
        await proxy.Completed(); Assert.Equal(2u, await proxy.CapturedSender());
        Assert.Equal(2u, await proxy.CompatibilitySender());
        Assert.Contains("async UniTask fault", (await Assert.ThrowsAsync<RpcException>(async () => await proxy.Fail())).Message);
        Assert.Null(NetRpcCallContext.Current);
    }

    private sealed class DeferredReply : NetTransport
    {
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public NetRpcModel Last;
        public bool ReplyInline;
        public UniTask Send(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { var model = NetRpcCodec.Decode(bytes); Last = new NetRpcModel(model.Kind, model.TargetId, model.MethodId, model.RequestId, 0, default); if (ReplyInline) Reply(42); return UniTask.CompletedTask; }
        public UniTask SendFast(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => Send(bytes, cancellationToken);
        public void Reply(int value)
        {
            using var bag = NetMessageBag.Pool(); bag.Write(value);
            OnReceived?.Invoke(NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Return, Last.TargetId, Last.MethodId, Last.RequestId, bag.Count, bag.Memory)));
        }
    }
    private static NetRpcModel Call => new(NetRpcMessageKind.Call, 7, 8, 0, 0, default);

    [Fact]
    public async Task InlineContinuationCanReenterAndReuseWaiterOutsideRuntimeGates()
    {
        var transport = new DeferredReply(); using var services = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(services, transport, false); var context = runtime.CreateContext(7);
        var gate = typeof(RpcContextService).GetField("_requestGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        async UniTask<int> Reenter()
        {
            int first = await context.Request<int>(Call);
            Assert.False(Monitor.IsEntered(gate));
            transport.ReplyInline = true;
            return first + await context.Request<int>(Call);
        }
        var operation = Reenter(); transport.Reply(42); Assert.Equal(84, await operation);
        var consumed = context.Request<int>(Call); Assert.Equal(42, await consumed);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await consumed);
        Assert.Equal(42, await context.Request<int>(Call));
    }

    [Fact]
    public async Task CancellationPreservesUniTaskStatusAndExplicitTaskCompatibilityStatus()
    {
        var transport = new DeferredReply(); using var services = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(services, transport, false); var context = runtime.CreateContext(7);
        using var cancellation = new CancellationTokenSource();
        var operation = context.Request<int>(Call, cancellation.Token); cancellation.Cancel();
        Assert.Equal(UniTaskStatus.Canceled, operation.Status);
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation)).CancellationToken);
        var compatible = context.RequestTask<int>(Call, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compatible); Assert.True(compatible.IsCanceled);
    }

    [Fact]
    public async Task ConcurrentUniTaskWhenAllUsesTypedResultsAcrossPooledGenerations()
    {
        var pair = Providers(); using var host = pair.Host; using var client = pair.Client;
        var proxy = client.GetRequiredService<IAsyncApi>();
        for (int batch = 0; batch < 3; batch++)
        {
            var expected = Enumerable.Range(batch * 128, 128).ToArray();
            Assert.Equal(expected, await UniTask.WhenAll(expected.Select(proxy.Delayed)));
            await UniTask.WhenAll(Enumerable.Range(0, 16).Select(_ => proxy.Completed()));
        }
    }
}
