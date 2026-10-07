using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.RemoteCompiler;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Xunit;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Tests;

public sealed class NetworkObjectComponentReadyTests
{
    public interface IInitialState
    {
        UniTask<byte[]> Read(uint entityId);
    }

    public sealed class InitialState : IInitialState
    {
        private readonly RpcContextService _runtime;
        public InitialState(RpcContextService runtime) => _runtime = runtime;
        public UniTask<byte[]> Read(uint entityId) => _runtime.CaptureEntityStateAsync(entityId);
    }

    private sealed class EntityLease : IDisposable
    {
        private readonly ServiceProvider _provider;
        public EntityLease(uint entityId, params INetComponent[] components)
        {
            var services = new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(entityId));
            foreach (var component in components) services.AddSingleton(component);
            _provider = services.BuildServiceProvider(); Entity = new NetEntity(_provider);
        }
        public NetEntity Entity { get; }
        public void Dispose() => _provider.Dispose();
    }

    private sealed class RuntimePair : IDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        public readonly EntitiesService HostEntities = new(), ClientEntities = new();
        public readonly RpcContextService Host, Client;
        public readonly NetRpcDesignTests.Pair ClientWire, HostWire;
        public RuntimePair(ulong scope = 113)
        {
            (ClientWire, HostWire) = NetRpcDesignTests.Pair.Create();
            Host = new RpcContextService(_provider, true, scope); Client = new RpcContextService(_provider, false, scope);
            Host.AttachEntities(HostEntities); Client.AttachEntities(ClientEntities);
            Host.AttachPeer(2, HostWire); Client.AttachPeer(1, ClientWire);
        }
        public void Dispose() { Client.Dispose(); Host.Dispose(); _provider.Dispose(); }
    }

    private sealed class ObservedTransport : NetTransport, IDisposable
    {
        private readonly NetTransport _inner;
        public readonly TaskCompletionSource ComponentObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public ObservedTransport(NetTransport inner) { _inner = inner; inner.OnReceived += Receive; }
        private void Receive(ReadOnlyMemory<byte> bytes)
        {
            OnReceived?.Invoke(bytes);
            if (NetRpcCodec.Decode(bytes).Kind == NetRpcMessageKind.Component) ComponentObserved.TrySetResult();
        }
        public UniTask Send(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => _inner.Send(bytes, token);
        public UniTask SendFast(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => _inner.SendFast(bytes, token);
        public void Dispose() => _inner.OnReceived -= Receive;
    }

    [Fact]
    public async Task ActualGeneratedReliableRpcRecoversUdpReceivedBeforeRegistrationOverNativeSockets()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        var accepted = listener.AcceptAsync();
        await using var clientSocket = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port);
        await using var hostSocket = await accepted;
        using var clientWire = new ObservedTransport(clientSocket);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var host = new RpcContextService(provider, true, 501);
        using var client = new RpcContextService(provider, false, 501);
        var hostEntities = new EntitiesService(); var clientEntities = new EntitiesService();
        host.AttachEntities(hostEntities); client.AttachEntities(clientEntities);
        host.AttachPeer(2, hostSocket); client.AttachPeer(1, clientWire);
        var authoritative = new NetComponent<int>(1, 87); var local = new NetComponent<int>(1);
        using var hostEntity = new EntityLease(17, authoritative); using var clientEntity = new EntityLease(17, local);
        hostEntities.Register(hostEntity.Entity);

        // The first UDP snapshot has really been dispatched and discarded before async
        // object construction finishes. No synchronization timer or second UDP is required.
        await host.PublishStateAsync();
        await clientWire.ComponentObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clientEntities.Register(clientEntity.Entity);
        Assert.Equal(0, local.Value);

        var factory = new RemoteInterfaceCompiler(); var target = RpcContextService.ContractId(typeof(IInitialState));
        host.RegisterTarget(target, new InitialState(host), typeof(IInitialState));
        factory.RegisterReceiver(typeof(IInitialState), host, target);
        var proxy = (IInitialState)factory.Create(typeof(IInitialState), client.CreateContext(target));
        Assert.StartsWith("NetRemote_", proxy.GetType().Name);
        var snapshot = await proxy.Read(17).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, local.Value); // An RPC reply alone is not object readiness.
        var changes = 0; local.Changed += (_, _) => changes++;
        client.ApplyEntityState(clientEntity.Entity, snapshot);
        Assert.Equal(87, local.Value); Assert.Equal(1, changes);
        client.ApplyEntityState(clientEntity.Entity, snapshot);
        Assert.Equal(1, changes);

        var updated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        local.Changed += (_, value) => updated.TrySetResult(value);
        authoritative.Value = 64; await host.PublishStateAsync();
        Assert.Equal(64, await updated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        client.ApplyEntityState(clientEntity.Entity, snapshot);
        Assert.Equal(64, local.Value); Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ReliableReadDoesNotConsumePendingComponentBroadcastToExistingClients()
    {
        using var pair = new RuntimePair();
        var authoritative = new NetComponent<int>(1, 1); var local = new NetComponent<int>(1);
        using var hostEntity = new EntityLease(7, authoritative); using var clientEntity = new EntityLease(7, local);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(clientEntity.Entity);
        await pair.Host.PublishStateAsync(); Assert.Equal(1, local.Value);
        authoritative.Value = 2;
        var requested = await pair.Host.CaptureEntityStateAsync(7);
        Assert.Equal(1, local.Value);
        await pair.Host.PublishStateAsync(); Assert.Equal(2, local.Value);
        var sends = pair.HostWire.FastCount;
        await pair.Host.PublishStateAsync(); Assert.Equal(sends, pair.HostWire.FastCount);
        pair.Client.ApplyEntityState(clientEntity.Entity, requested);
        Assert.Equal(2, local.Value);
    }

    [Fact]
    public async Task ClientRegistrationResetsLocalInitializationAndReusedReceiveRevisionButPreservesHostState()
    {
        using var pair = new RuntimePair();
        var authoritative = new NetComponent<int>(1, 10); authoritative.Value = 11;
        var local = new NetComponent<int>(1); local.Value = -1; local.Value = -2;
        using var hostEntity = new EntityLease(7, authoritative); using var clientEntity = new EntityLease(7, local);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(clientEntity.Entity);
        Assert.Equal(11, authoritative.Value); Assert.Equal(1, authoritative.Revision);
        Assert.Equal(-2, local.Value); Assert.Equal(0, local.Revision);
        var changed = 0; local.Changed += (_, _) => changed++;
        var snapshot = await pair.Host.CaptureEntityStateAsync(7);
        pair.Client.ApplyEntityState(clientEntity.Entity, snapshot);
        Assert.Equal(11, local.Value); Assert.Equal(1, local.Revision); Assert.Equal(1, changed);
        pair.Client.ApplyEntityState(clientEntity.Entity, snapshot); Assert.Equal(1, changed);

        pair.ClientEntities.Unregister(clientEntity.Entity);
        using var replacementWorld = new RuntimePair(114);
        using var replacementHost = new EntityLease(7, new NetComponent<int>(1, 7));
        replacementWorld.HostEntities.Register(replacementHost.Entity);
        replacementWorld.ClientEntities.Register(clientEntity.Entity);
        Assert.Equal(11, local.Value); Assert.Equal(0, local.Revision);
        var replacement = await replacementWorld.Host.CaptureEntityStateAsync(7);
        replacementWorld.Client.ApplyEntityState(clientEntity.Entity, replacement);
        Assert.Equal(7, local.Value); Assert.Equal(0, local.Revision); Assert.Equal(2, changed);
        replacementWorld.Client.ApplyEntityState(clientEntity.Entity, replacement); Assert.Equal(2, changed);
    }

    [Fact]
    public async Task ManifestRejectsMissingUnknownAndDuplicateComponentsBeforeApplyingAnyValue()
    {
        using var pair = new RuntimePair();
        using var hostEntity = new EntityLease(7, new NetComponent<int>(1, 10), new NetComponent<int>(2, 20));
        var first = new NetComponent<int>(1); var second = new NetComponent<int>(2);
        using var clientEntity = new EntityLease(7, first, second);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(clientEntity.Entity);
        var snapshot = await pair.Host.CaptureEntityStateAsync(7); var frames = ReadFrames(snapshot);
        AssertInvalid(() => pair.Client.ApplyEntityState(clientEntity.Entity, Manifest(pair.Client.Scope, 7, frames.Take(1).ToArray())));
        AssertInvalid(() => pair.Client.ApplyEntityState(clientEntity.Entity, Manifest(pair.Client.Scope, 7, new[] { frames[0], frames[0] })));
        var model = NetRpcCodec.Decode(frames[1]);
        var unknown = NetRpcCodec.Encode(new NetRpcModel(model.Kind, model.TargetId, 999, 0, model.ArgumentCount, model.Payload, model.Scope));
        AssertInvalid(() => pair.Client.ApplyEntityState(clientEntity.Entity, Manifest(pair.Client.Scope, 7, new[] { frames[0], unknown })));
        Assert.Equal(0, first.Value); Assert.Equal(0, second.Value);
        pair.Client.ApplyEntityState(clientEntity.Entity, snapshot);
        Assert.Equal(10, first.Value); Assert.Equal(20, second.Value);
    }

    [Fact]
    public async Task ManifestRejectsSchemaIdentityScopeAndFingerprintMismatch()
    {
        using var pair = new RuntimePair();
        using var hostEntity = new EntityLease(7, new NetComponent<int>(1, 10), new NetComponent<string>(2, "value"));
        var first = new NetComponent<int>(1); var incompatible = new NetComponent<int>(2);
        using var badSchema = new EntityLease(7, first, incompatible);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(badSchema.Entity);
        var snapshot = await pair.Host.CaptureEntityStateAsync(7);
        AssertInvalid(() => pair.Client.ApplyEntityState(badSchema.Entity, snapshot));
        Assert.Equal(0, first.Value); Assert.Equal(0, incompatible.Value);
        pair.ClientEntities.Unregister(badSchema.Entity);
        using var goodSchema = new EntityLease(7, first, new NetComponent<string>(2)); pair.ClientEntities.Register(goodSchema.Entity);
        var frames = ReadFrames(snapshot);
        AssertInvalid(() => pair.Client.ApplyEntityState(goodSchema.Entity, Manifest(999, 7, frames)));
        AssertInvalid(() => pair.Client.ApplyEntityState(goodSchema.Entity, Manifest(pair.Client.Scope, 8, frames)));
        var wrongScope = NetRpcCodec.Decode(frames[0]);
        var changed = NetRpcCodec.Encode(new NetRpcModel(wrongScope.Kind, 7, wrongScope.MethodId, 0, wrongScope.ArgumentCount, wrongScope.Payload, 999));
        AssertInvalid(() => pair.Client.ApplyEntityState(goodSchema.Entity, Manifest(pair.Client.Scope, 7, new[] { changed, frames[1] })));
        var corrupt = frames[0].ToArray(); corrupt[^1] ^= 1;
        AssertInvalid(() => pair.Client.ApplyEntityState(goodSchema.Entity, Manifest(pair.Client.Scope, 7, new[] { corrupt, frames[1] })));
        Assert.Equal(0, first.Value);
    }

    [Fact]
    public async Task LateSnapshotCannotApplyToReplacementEntityOrCompleteReentrantDespawn()
    {
        using var pair = new RuntimePair();
        using var hostEntity = new EntityLease(7, new NetComponent<int>(1, 42));
        var oldValue = new NetComponent<int>(1); var newValue = new NetComponent<int>(1);
        using var oldEntity = new EntityLease(7, oldValue); using var newEntity = new EntityLease(7, newValue);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(oldEntity.Entity);
        var snapshot = await pair.Host.CaptureEntityStateAsync(7);
        pair.ClientEntities.Unregister(oldEntity.Entity); pair.ClientEntities.Register(newEntity.Entity);
        Assert.Equal(RpcError.MissingTarget, Assert.Throws<RpcException>(() => pair.Client.ApplyEntityState(oldEntity.Entity, snapshot)).Error);
        Assert.Equal(0, oldValue.Value); Assert.Equal(0, newValue.Value);

        newValue.Changed += (_, _) => pair.ClientEntities.Unregister(newEntity.Entity);
        Assert.Equal(RpcError.MissingTarget, Assert.Throws<RpcException>(() => pair.Client.ApplyEntityState(newEntity.Entity, snapshot)).Error);
        Assert.Empty(pair.ClientEntities.Entities);
    }

    [Fact]
    public async Task EmptyRegisteredEntityHasExplicitScopeWhileMissingEntityAndInvalidRolesFail()
    {
        using var pair = new RuntimePair();
        Assert.Same(pair.HostEntities, pair.Host.Entities);
        Assert.Equal(RpcError.MissingTarget, (await Assert.ThrowsAsync<RpcException>(() => pair.Host.CaptureEntityStateAsync(7).AsTask())).Error);
        using var hostEntity = new EntityLease(7); using var clientEntity = new EntityLease(7);
        pair.HostEntities.Register(hostEntity.Entity); pair.ClientEntities.Register(clientEntity.Entity);
        var snapshot = await pair.Host.CaptureEntityStateAsync(7);
        Assert.Empty(ReadFrames(snapshot)); pair.Client.ApplyEntityState(clientEntity.Entity, snapshot);
        AssertInvalid(() => pair.Client.ApplyEntityState(clientEntity.Entity, Manifest(999, 7, Array.Empty<byte[]>())));
        Assert.Equal(RpcError.InvalidRole, (await Assert.ThrowsAsync<RpcException>(() => pair.Client.CaptureEntityStateAsync(7).AsTask())).Error);
        Assert.Equal(RpcError.InvalidRole, Assert.Throws<RpcException>(() => pair.Host.ApplyEntityState(hostEntity.Entity, snapshot)).Error);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pair.Host.CaptureEntityStateAsync(7, cancellation.Token).AsTask());
    }

    private sealed class HeldTransport : NetTransport
    {
        public event Action<ReadOnlyMemory<byte>>? OnReceived { add { } remove { } }
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UniTask Send(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => UniTask.CompletedTask;
        public async UniTask SendFast(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task; }
    }

    [Fact]
    public async Task SnapshotCaptureCancellationInterruptsPublishGateWithoutPoisoningFutureCapture()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var wire = new HeldTransport(); using var host = new RpcContextService(provider, wire, true);
        var entities = new EntitiesService(); host.AttachEntities(entities);
        using var entity = new EntityLease(7, new NetComponent<int>(1, 10)); entities.Register(entity.Entity);
        var publication = host.PublishStateAsync().AsTask(); await wire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var capture = host.CaptureEntityStateAsync(7, cancellation.Token).AsTask(); Assert.False(capture.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        wire.Release.TrySetResult(); await publication;
        Assert.NotEmpty(await host.CaptureEntityStateAsync(7));
    }

    private sealed class ThreadBoundComponent : INetComponent
    {
        private readonly NetComponent<int> _inner = new(1, 10);
        public int ExpectedThread, ThreadChecks;
        private void CheckThread()
        {
            if (ExpectedThread == 0) return;
            Assert.Equal(ExpectedThread, Environment.CurrentManagedThreadId); ThreadChecks++;
        }
        public uint ComponentId => 1;
        public ulong SchemaFingerprint { get { CheckThread(); return _inner.SchemaFingerprint; } }
        public ulong Fingerprint { get { CheckThread(); return _inner.Fingerprint; } }
        public long Revision => _inner.Revision;
        public void SetAuthority(bool authority) => _inner.SetAuthority(authority);
        public void WriteSnapshot(NetMessageBag bag) { CheckThread(); _inner.WriteSnapshot(bag); }
        public long CaptureSnapshot(NetMessageBag bag) { CheckThread(); return _inner.CaptureSnapshot(bag); }
        public void ApplySnapshot(NetMessageReader reader, long revision) => _inner.ApplySnapshot(reader, revision);
    }

    private sealed class HeldThreadSwitch : IUniTaskSource
    {
        private readonly UniTaskCompletionSource _source = new();
        public readonly TaskCompletionSource Awaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UniTask Switch(CancellationToken token) { token.ThrowIfCancellationRequested(); return new UniTask(this, 0); }
        public void Release() => _source.TrySetResult();
        public void GetResult(short token) => ((IUniTaskSource)_source).GetResult(token);
        public UniTaskStatus GetStatus(short token) => ((IUniTaskSource)_source).GetStatus(token);
        public UniTaskStatus UnsafeGetStatus() => ((IUniTaskSource)_source).UnsafeGetStatus();
        public void OnCompleted(Action<object> continuation, object state, short token)
        {
            ((IUniTaskSource)_source).OnCompleted(continuation, state, token);
            Awaiting.TrySetResult();
        }
    }

    [Fact]
    public async Task SnapshotCaptureRestoresComponentThreadAfterContendedPublicationGate()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var wire = new HeldTransport(); using var host = new RpcContextService(provider, wire, true);
        var entities = new EntitiesService(); host.AttachEntities(entities);
        var component = new ThreadBoundComponent(); using var entity = new EntityLease(7, component); entities.Register(entity.Entity);
        var publication = host.PublishStateAsync().AsTask(); await wire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatcher = new HeldThreadSwitch();
        var capture = host.CaptureEntityStateAsync(7, default, dispatcher.Switch).AsTask();
        Assert.False(capture.IsCompleted); Assert.False(dispatcher.Awaiting.Task.IsCompleted);
        wire.Release.TrySetResult(); await publication;
        await dispatcher.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var engineThread = new Thread(() =>
        {
            component.ExpectedThread = Environment.CurrentManagedThreadId;
            dispatcher.Release();
        });
        engineThread.Start();
        Assert.NotEmpty(await capture.WaitAsync(TimeSpan.FromSeconds(5)));
        // Completion deliberately resumes inline on the engine thread. Joining it
        // from this continuation would wait for ourselves to return from Release.
        Assert.True(await Task.Run(() => engineThread.Join(TimeSpan.FromSeconds(5))));
        Assert.True(component.ThreadChecks >= 3);
    }

    [Fact]
    public async Task ReliableInitialStateRequestFailsWithoutHostAndCancelsWhileReplyIsPending()
    {
        using var pair = new RuntimePair();
        var target = RpcContextService.ContractId(typeof(IInitialState));
        pair.Host.RegisterTarget(target, new InitialState(pair.Host), typeof(IInitialState));
        using var hostEntity = new EntityLease(7, new NetComponent<int>(1, 5)); pair.HostEntities.Register(hostEntity.Entity);
        using var bag = NetMessageBag.Pool(); bag.Write(7u);
        var call = new NetRpcModel(NetRpcMessageKind.Call, target,
            RpcContextService.RpcMethodId(typeof(IInitialState), typeof(IInitialState).GetMethod(nameof(IInitialState.Read))!), 0, bag.Count, bag.Memory);
        var context = pair.Client.CreateContext(target);
        pair.HostWire.Drop = bytes => NetRpcCodec.Decode(bytes).Kind == NetRpcMessageKind.Return;
        using var cancellation = new CancellationTokenSource();
        var request = context.Request<byte[]>(call, cancellation.Token).AsTask(); Assert.False(request.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var waiting = context.Request<byte[]>(call).AsTask(); pair.Client.DetachPeer(1);
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => waiting)).Error);
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => context.Request<byte[]>(call).AsTask())).Error);
    }

    private static void AssertInvalid(Action action) => Assert.Equal(RpcError.InvalidPayload, Assert.Throws<RpcException>(action).Error);
    private static byte[][] ReadFrames(byte[] snapshot)
    {
        using var reader = new NetMessageReader(snapshot, 3);
        _ = reader.Read<ulong>(); _ = reader.Read<uint>(); var frames = reader.Read<byte[][]>(); reader.Complete(); return frames;
    }
    private static byte[] Manifest(ulong scope, uint entityId, byte[][] frames)
    {
        using var bag = NetMessageBag.Pool(); bag.Write(scope); bag.Write(entityId); bag.Write(frames); return bag.Memory.ToArray();
    }
}
