using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class NetworkObjectTests
{
    private sealed class Identity : INetworkIdentity { public uint EntityId { get; set; } }
    private sealed class Instance
    {
        public readonly Identity Identity = new();
        public readonly NetComponent<int> Health = new(1, 73);
        public bool Released;
        public uint Owner;
        public bool Active;
    }
    private sealed class Adapter : INetworkObjectAdapter
    {
        public Func<CancellationToken, Task>? Loading;
        public int FailLoads;
        public bool OmitEntity;
        public bool SceneAvailable = true;
        public bool ReuseScene;
        public Instance? SceneInstance;
        public int StaleSceneReleases;
        public readonly ConcurrentBag<Instance> Created = new();
        public int Releases;
        public async UniTask<NetworkObjectInstance?> InstantiateAsync(NetworkObjectState state, CancellationToken token)
        {
            if (Loading != null) await Loading(token);
            if (Interlocked.CompareExchange(ref FailLoads, 0, 1) == 1) throw new InvalidOperationException("load failed");
            if (state.Kind == NetworkObjectKind.Scene && !SceneAvailable) return null;
            var value = ReuseScene && state.Kind == NetworkObjectKind.Scene ? SceneInstance ??= new Instance() : new Instance(); Created.Add(value);
            var provider = new ServiceCollection().AddSingleton<INetworkIdentity>(value.Identity)
                .AddSingleton<INetComponent>(value.Health).BuildServiceProvider();
            return new NetworkObjectInstance(value, value.Identity, OmitEntity ? null : new NetEntity(provider));
        }
        public void Bind(NetworkObjectInstance instance, NetworkObjectState state, bool authority)
        { ((Instance)instance.Instance).Identity.EntityId = state.EntityId; ApplyState(instance, state); }
        public void ApplyState(NetworkObjectInstance instance, NetworkObjectState state)
        { var value = (Instance)instance.Instance; value.Owner = state.OwnerPeerId; value.Active = state.Active; }
        public void Release(NetworkObjectInstance instance, NetworkObjectState state)
        {
            var value = (Instance)instance.Instance;
            if (state.Kind == NetworkObjectKind.Scene && value.Identity.EntityId != 0 && value.Identity.EntityId != state.EntityId)
            { Interlocked.Increment(ref StaleSceneReleases); return; }
            Assert.False(value.Released); value.Released = true; value.Identity.EntityId = 0; Interlocked.Increment(ref Releases);
        }
    }
    private sealed class World : IDisposable
    {
        public readonly ServiceProvider Provider;
        public readonly RpcContextService Runtime;
        public readonly NetworkObjectService Objects;
        public readonly IEntitiesService Entities;
        public readonly Adapter Adapter;
        public readonly ConcurrentQueue<Exception> Errors = new();
        public World(bool host, Adapter? adapter = null, ulong scope = 901, uint generation = 1)
        {
            Adapter = adapter ?? new Adapter();
            Provider = new ServiceCollection().AddNetRpcRuntime(host, scope).BuildServiceProvider();
            Runtime = Provider.GetRequiredService<RpcContextService>();
            Entities = Provider.GetRequiredService<IEntitiesService>();
            Objects = new NetworkObjectService(Runtime, Adapter, Entities, generation, host ? 1u : 2u);
            Runtime.Faulted += Errors.Enqueue; Objects.Faulted += Errors.Enqueue;
        }
        public void Dispose() { Objects.Dispose(); Provider.Dispose(); }
    }
    private static (NetRpcDesignTests.Pair Client, NetRpcDesignTests.Pair Host) Connect(World host, World client)
    {
        var (a,b) = NetRpcDesignTests.Pair.Create(); host.Runtime.AttachPeer(2,b); client.Runtime.AttachPeer(1,a); return (a,b);
    }
    private static NetworkObjectState Template(string address = "test/cube") => new()
    { Kind = NetworkObjectKind.Prefab, Address = address, RotationW = 1, Active = true, OwnerPeerId = 2 };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task SpawnOwnershipDespawnUseExistingEntitiesAndRejectClientMutation()
    {
        using var host = new World(true); using var client = new World(false); Connect(host, client);
        var ready = new List<uint>(); client.Objects.Spawned += h => ready.Add(h.EntityId);
        var item = await host.Objects.SpawnAsync(Template());
        await Until(() => client.Objects.TryGet(item.EntityId, out _));
        Assert.Single(host.Entities.Entities); Assert.Single(client.Entities.Entities); Assert.Single(ready);
        Assert.True(client.Objects.Owns(item.EntityId));
        Assert.Throws<RpcException>(() => client.Objects.SetOwner(item.EntityId, 1));
        await Assert.ThrowsAsync<RpcException>(() => client.Objects.SpawnAsync(Template()).AsTask());
        await Assert.ThrowsAsync<RpcException>(() => client.Objects.DespawnAsync(item.EntityId).AsTask());
        host.Objects.SetOwner(item.EntityId, 1);
        await Until(() => !client.Objects.Owns(item.EntityId));
        await host.Objects.DespawnAsync(item.EntityId);
        await Until(() => !client.Objects.TryGet(item.EntityId, out _));
        Assert.Empty(host.Entities.Entities); Assert.Empty(client.Entities.Entities);
        Assert.Equal(1, host.Adapter.Releases); Assert.Equal(1, client.Adapter.Releases);
        Assert.Empty(host.Errors); Assert.Empty(client.Errors);
    }

    [Fact]
    public async Task LateJoinAndRepeatedSnapshotAreIdempotentAndStateReady()
    {
        using var host = new World(true);
        var item = await host.Objects.SpawnAsync(Template()); ((Instance)item.Instance).Health.Value = 41;
        using var client = new World(false); Connect(host, client);
        int spawned = 0; client.Objects.Spawned += h => { Assert.Equal(41, ((Instance)h.Instance).Health.Value); spawned++; };
        await client.Objects.SynchronizeAsync(); await client.Objects.SynchronizeAsync();
        Assert.Equal(1, spawned); Assert.Single(client.Objects.Objects); Assert.Single(client.Entities.Entities);
        Assert.Empty(client.Errors);
    }

    [Fact]
    public async Task StateBeforeAsyncLoadIsRecoveredBeforeSpawnedAndOwnerChangeDoesNotLoseObject()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new World(true); using var client = new World(false, new Adapter { Loading = _ => gate.Task }); Connect(host, client);
        int ready = 0; client.Objects.Spawned += h => { Assert.Equal(19, ((Instance)h.Instance).Health.Value); Assert.Equal(1u, h.OwnerPeerId); ready++; };
        var item = await host.Objects.SpawnAsync(Template());
        ((Instance)item.Instance).Health.Value = 19; await host.Runtime.PublishStateAsync(true);
        host.Objects.SetOwner(item.EntityId, 1); Assert.Empty(client.Entities.Entities);
        gate.SetResult(); await Until(() => ready == 1);
        Assert.Single(client.Objects.Objects); Assert.Empty(client.Errors);
    }

    [Fact]
    public async Task DespawnDuringLoadReleasesLateResultAndDoesNotResurrect()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new World(true); using var client = new World(false, new Adapter { Loading = _ => gate.Task }); Connect(host, client);
        int ready = 0; client.Objects.Spawned += _ => ready++;
        var item = await host.Objects.SpawnAsync(Template()); await host.Objects.DespawnAsync(item.EntityId);
        gate.SetResult(); await Until(() => client.Adapter.Releases == 1);
        Assert.Equal(0, ready); Assert.Empty(client.Objects.Objects); Assert.Empty(client.Entities.Entities);
    }

    [Fact]
    public async Task DisposeDuringLoadReleasesIgnoringCancellationAdapterResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new World(true); using var client = new World(false, new Adapter { Loading = _ => gate.Task }); Connect(host, client);
        await host.Objects.SpawnAsync(Template()); client.Objects.Dispose(); gate.SetResult();
        await Until(() => client.Adapter.Releases == 1);
        Assert.Empty(client.Entities.Entities); Assert.Empty(client.Objects.Objects);
    }

    [Fact]
    public async Task FailedClientLoadCanRetryWithoutDuplicateEntity()
    {
        using var host = new World(true); using var client = new World(false, new Adapter { FailLoads = 1 }); Connect(host, client);
        var item = await host.Objects.SpawnAsync(Template()); await Until(() => !client.Errors.IsEmpty);
        await client.Objects.RetryPendingAsync();
        Assert.True(client.Objects.TryGet(item.EntityId, out _)); Assert.Single(client.Entities.Entities);
        Assert.Single(client.Errors);
    }

    [Fact]
    public async Task OutOfOrderEntitiesAndDuplicatePacketsDoNotLoseRosterOrResurrectTombstones()
    {
        using var host = new World(true); using var client = new World(false); var wire = Connect(host, client);
        wire.Host.Drop = bytes => NetRpcCodec.Decode(bytes).Kind == NetRpcMessageKind.Call;
        var first = await host.Objects.SpawnAsync(Template("test/first"));
        var second = await host.Objects.SpawnAsync(Template("test/second"));
        var packets = wire.Host.Sent.Where(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.Call).ToArray();
        Assert.Equal(2, packets.Length); wire.Host.Drop = null;
        wire.Client.Deliver(packets[1]); wire.Client.Deliver(packets[0]); wire.Client.Deliver(packets[1]);
        await Until(() => client.Objects.Objects.Count == 2); Assert.Equal(2, client.Adapter.Created.Count);
        await host.Objects.DespawnAsync(first.EntityId); wire.Client.Deliver(packets[0]);
        Assert.False(client.Objects.TryGet(first.EntityId, out _)); Assert.True(client.Objects.TryGet(second.EntityId, out _));
    }

    [Fact]
    public async Task UnloadedSceneRetriesAfterSceneBecomesAvailable()
    {
        using var host = new World(true); using var client = new World(false, new Adapter { SceneAvailable = false }); Connect(host, client);
        var item = await host.Objects.SpawnAsync(new NetworkObjectState { Kind = NetworkObjectKind.Scene, SceneKey = "map/root/door", RotationW = 1, Active = true });
        Assert.Empty(client.Objects.Objects); client.Adapter.SceneAvailable = true; await client.Objects.RetryPendingAsync();
        Assert.True(client.Objects.TryGet(item.EntityId, out var found)); Assert.True(found!.IsSceneObject);
    }

    [Fact]
    public async Task FailedHostInitializationRollsBackAndDoesNotReuseId()
    {
        using var host = new World(true);
        Func<NetworkObjectHandle,CancellationToken,UniTask> fail = (_,_) => UniTask.FromException(new InvalidOperationException("initialization failed"));
        host.Objects.Initializing += fail;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Objects.SpawnAsync(Template()).AsTask());
        Assert.Empty(host.Objects.Objects); Assert.Empty(host.Entities.Entities); Assert.Equal(1, host.Adapter.Releases);
        host.Objects.Initializing -= fail; var next = await host.Objects.SpawnAsync(Template()); Assert.Equal(2u, next.EntityId);
    }

    [Fact]
    public async Task ForgedClientRosterMutationIsRejectedByAuthenticatedDirection()
    {
        using var host = new World(true); using var client = new World(false); var wire = Connect(host, client);
        await host.Objects.SpawnAsync(Template());
        var original = NetRpcCodec.Decode(wire.Host.Sent.First(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.Call));
        var forged = Template(); forged.EntityId = 999; forged.WorldGeneration = 1; forged.Revision = 999;
        using var bag = NetMessageBag.Pool(); bag.Write(forged);
        wire.Host.Deliver(NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Call, original.TargetId, original.MethodId, 0, bag.Count, bag.Memory, 901)));
        await Until(() => !host.Errors.IsEmpty);
        Assert.False(host.Objects.TryGet(999, out _)); Assert.Single(host.Objects.Objects);
        Assert.Contains(host.Errors, e => e is RpcException rpc && rpc.Error == RpcError.InvalidRole);
    }

    [Fact]
    public async Task NewWorldMayReuseIdButRejectsPreviousScopeAndGeneration()
    {
        byte[] old;
        using (var host = new World(true))
        using (var client = new World(false))
        {
            var wire = Connect(host, client); await host.Objects.SpawnAsync(Template());
            old = wire.Host.Sent.First(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.Call);
        }
        using var nextHost = new World(true, scope: 902, generation: 2);
        using var nextClient = new World(false, scope: 902, generation: 2);
        var nextWire = Connect(nextHost, nextClient); nextWire.Client.Deliver(old);
        Assert.Empty(nextClient.Objects.Objects);
        var item = await nextHost.Objects.SpawnAsync(Template("test/new-world")); Assert.Equal(1u, item.EntityId);
        await Until(() => nextClient.Objects.TryGet(1, out _));
        var prior = NetRpcCodec.Decode(old);
        nextWire.Client.Deliver(NetRpcCodec.Encode(new NetRpcModel(prior.Kind, prior.TargetId, prior.MethodId, prior.RequestId, prior.ArgumentCount, prior.Payload, 902)));
        Assert.True(nextClient.Objects.TryGet(1, out var current)); Assert.Equal("test/new-world", current!.State.Address);
    }

    [Fact]
    public async Task FreshClientReconnectRebuildsCurrentRosterWithoutRemovedObjects()
    {
        using var host = new World(true);
        uint retained;
        using (var firstClient = new World(false))
        {
            Connect(host, firstClient);
            var removed = await host.Objects.SpawnAsync(Template("test/removed"));
            var keep = await host.Objects.SpawnAsync(Template("test/retained")); retained = keep.EntityId;
            await host.Objects.DespawnAsync(removed.EntityId);
            ((Instance)keep.Instance).Health.Value = 11;
            host.Runtime.DetachPeer(2); firstClient.Runtime.DetachPeer(1);
        }
        using var reconnected = new World(false); Connect(host, reconnected);
        await reconnected.Objects.SynchronizeAsync();
        Assert.Single(reconnected.Objects.Objects); Assert.True(reconnected.Objects.TryGet(retained, out var restored));
        Assert.Equal(11, ((Instance)restored!.Instance).Health.Value);
        Assert.Equal("test/retained", restored.State.Address);
        Assert.Empty(reconnected.Errors);
    }

    [Fact]
    public async Task CancellationDuringHostLoadCleansUpWithoutAnnouncing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new World(true, new Adapter { Loading = _ => gate.Task }); using var client = new World(false); Connect(host, client);
        using var cancellation = new CancellationTokenSource();
        var spawn = host.Objects.SpawnAsync(Template(), cancellation.Token).AsTask();
        cancellation.Cancel(); gate.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spawn);
        await Until(() => host.Adapter.Releases == 1);
        Assert.Empty(host.Objects.Objects); Assert.Empty(host.Entities.Entities); Assert.Empty(client.Objects.Objects);
        Assert.Equal(1, host.Adapter.Releases);
    }

    [Fact]
    public async Task MissingClientEntityIntegrationCannotReportComponentReady()
    {
        using var host = new World(true); using var client = new World(false, new Adapter { OmitEntity = true }); Connect(host, client);
        int ready = 0; client.Objects.Spawned += _ => ready++;
        await host.Objects.SpawnAsync(Template());
        await Until(() => !client.Errors.IsEmpty);
        Assert.Equal(0, ready); Assert.Empty(client.Objects.Objects); Assert.Empty(client.Entities.Entities);
        await Until(() => client.Adapter.Releases == 1);
    }

    [Fact]
    public async Task OldSceneInitializationCannotReleaseNewBindingOfSameEngineObject()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new World(true, new Adapter { ReuseScene = true });
        host.Objects.Initializing += async (handle, _) => { if (handle.EntityId == 1) await gate.Task; };
        var template = new NetworkObjectState { Kind = NetworkObjectKind.Scene, SceneKey = "map/door", Active = true };
        var first = host.Objects.SpawnAsync(template).AsTask();
        await Until(() => host.Adapter.SceneInstance?.Identity.EntityId == 1);
        await host.Objects.DespawnAsync(1);
        var second = await host.Objects.SpawnAsync(template);
        Assert.Equal(2u, second.EntityId); gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Until(() => host.Adapter.StaleSceneReleases == 1);
        Assert.Equal(2u, second.Identity.EntityId); Assert.False(((Instance)second.Instance).Released);
        Assert.Single(host.Entities.Entities); Assert.Single(host.Objects.Objects);
    }

    [Fact]
    public async Task InitialComponentCallbackDisposesWorldBeforeReadyWithoutResurrection()
    {
        using var host = new World(true); using var client = new World(false); Connect(host, client);
        int ready = 0;
        client.Objects.Initializing += (handle, _) =>
        {
            ((Instance)handle.Instance).Health.Changed += (_, _) => client.Objects.Dispose();
            return UniTask.CompletedTask;
        };
        client.Objects.Spawned += _ => ready++;
        await host.Objects.SpawnAsync(Template());
        await Until(() => client.Adapter.Releases == 1);
        Assert.Equal(0, ready); Assert.Empty(client.Objects.Objects); Assert.Empty(client.Entities.Entities);
    }

    [Fact]
    public async Task SuppliedLeaseIsReleasedWhenTemplateRejectedBeforeLoad()
    {
        using var host = new World(true);
        var instance = new Instance(); var lease = new NetworkObjectInstance(instance, instance.Identity);
        var invalid = Template(); invalid.Address = "";
        await Assert.ThrowsAsync<RpcException>(() => host.Objects.SpawnAsync(invalid, lease).AsTask());
        Assert.True(instance.Released); Assert.Equal(1, host.Adapter.Releases); Assert.Empty(host.Objects.Objects);
    }

    [Fact]
    public async Task DisposedWorldCannotReuseSameRuntimeScope()
    {
        using var world = new World(true); world.Objects.Dispose();
        Assert.Throws<InvalidOperationException>(() => new NetworkObjectService(world.Runtime, world.Adapter, world.Entities, 2, 1));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RealTcpUdpSpawnAndReliableLateJoinComponentSnapshot()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var accept = listener.AcceptAsync();
        await using var clientWire = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port); await using var hostWire = await accept;
        using var host = new World(true); using var client = new World(false);
        host.Runtime.AttachPeer(2, hostWire); client.Runtime.AttachPeer(1, clientWire);
        var item = await host.Objects.SpawnAsync(Template()); await Until(() => client.Objects.TryGet(item.EntityId, out _));
        ((Instance)item.Instance).Health.Value = 62; await host.Runtime.PublishStateAsync(true);
        await Until(() => client.Objects.TryGet(item.EntityId, out var h) && ((Instance)h!.Instance).Health.Value == 62);
        await host.Objects.DespawnAsync(item.EntityId); await Until(() => client.Objects.Objects.Count == 0);
        Assert.Empty(host.Errors); Assert.Empty(client.Errors);
    }
}
