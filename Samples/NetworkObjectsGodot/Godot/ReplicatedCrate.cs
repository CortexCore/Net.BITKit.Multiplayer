using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using Godot;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>A real engine Node supplies identity and components, but owns no network lifecycle rules.</summary>
public partial class ReplicatedCrate : Node3D, IGodotNetworkObject
{
    private ServiceProvider? _components;
    private NetworkObjectInstance? _lease;
    private bool _bound;
    public uint EntityId { get; private set; }
    public uint OwnerPeerId { get; private set; }
    public bool Authority { get; private set; }
    public bool SpawnedWithSnapshot { get; set; }
    public NetComponent<int> Health { get; } = new(1, -999);
    public NetEntity? Entity { get; private set; }

    public NetworkObjectInstance CreateNetworkInstance()
    {
        if (_components != null) throw new InvalidOperationException("Node already has an entity provider.");
        _components = new ServiceCollection().AddSingleton<INetworkIdentity>(this)
            .AddSingleton<INetComponent>(Health).BuildServiceProvider();
        Entity = new NetEntity(_components);
        return _lease = new NetworkObjectInstance(this, this, Entity);
    }

    public void BindNetworkIdentity(uint entityId, uint ownerPeerId, bool authority)
    {
        if (_bound && EntityId != entityId) throw new InvalidOperationException("Cannot rebind an active Node identity.");
        _bound = true; EntityId = entityId; OwnerPeerId = ownerPeerId; Authority = authority;
        Health.SetAuthority(authority);
    }

    public void ApplyNetworkOwner(uint owner) => OwnerPeerId = owner;
    public void ReleaseNetworkInstance(NetworkObjectInstance instance)
    {
        // Cleanup belongs to the lease; a late canceled load must not clear a newer binding.
        (instance.Entity?.ServiceProvider as IDisposable)?.Dispose();
        if (!ReferenceEquals(_lease, instance)) return;
        _lease = null; _components = null; Entity = null; _bound = false;
        SpawnedWithSnapshot = false; EntityId = 0; OwnerPeerId = 0;
    }

    public override void _Process(double delta)
    {
        if (!_bound) return;
        GetNode<Label3D>("Caption").Text = $"Entity {EntityId} · owner {OwnerPeerId}\nHP {Health.Value} · rev {Health.Revision}";
    }
}
