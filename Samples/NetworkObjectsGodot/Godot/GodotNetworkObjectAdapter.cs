using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using Godot;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>Engine-only creation/binding/transform/release. Core owns IDs, owner state and replication.</summary>
public sealed class GodotNetworkObjectAdapter : INetworkObjectAdapter, INetworkObjectDispatcher
{
    private readonly Node _root;
    private readonly GodotThread _thread;
    private readonly Dictionary<string, PackedScene> _prefabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node3D> _scenes = new(StringComparer.Ordinal);
    public int Instantiated { get; private set; }
    public int Bound { get; private set; }
    public int Released { get; private set; }
    public int MissingSceneAttempts { get; private set; }
    public GodotNetworkObjectAdapter(Node root, GodotThread thread) { _root = root; _thread = thread; }

    public void RegisterPrefab(string address, PackedScene prefab)
        => _thread.Invoke(() => _prefabs.Add(address, prefab));

    public void RegisterScene(string sceneKey, Node3D node)
        => _thread.Invoke(() => _scenes.Add(sceneKey, node));

    public UniTask<NetworkObjectInstance?> InstantiateAsync(NetworkObjectState state, CancellationToken token)
        => _thread.InvokeAsync<NetworkObjectInstance?>(() =>
        {
            Node3D node;
            if (state.Kind == NetworkObjectKind.Scene)
            {
                if (!_scenes.TryGetValue(state.SceneKey!, out node!)) { MissingSceneAttempts++; return null; }
            }
            else
            {
                if (!_prefabs.TryGetValue(state.Address!, out var packed))
                    throw new InvalidOperationException("Unknown local PackedScene address: " + state.Address);
                node = packed.Instantiate<Node3D>();
                node.Name = "NetworkCrate_" + state.EntityId;
                node.Visible = false;
                _root.AddChild(node); Instantiated++;
            }
            try { return RequireProvider(node).CreateNetworkInstance(); }
            catch
            {
                if (state.Kind != NetworkObjectKind.Scene) { _root.RemoveChild(node); node.QueueFree(); }
                throw;
            }
        }, token);

    public void Bind(NetworkObjectInstance instance, NetworkObjectState state, bool authority)
        => _thread.Invoke(() =>
        {
            var node = RequireNode(instance);
            RequireProvider(node).BindNetworkIdentity(state.EntityId, state.OwnerPeerId, authority); Bound++;
        });

    public void ApplyState(NetworkObjectInstance instance, NetworkObjectState state)
        => _thread.Invoke(() =>
        {
            var node = RequireNode(instance);
            if (instance.Identity.EntityId != state.EntityId)
                throw new InvalidOperationException("Cannot apply stale object state to a different Node binding.");
            node.Position = new Vector3(state.PositionX, state.PositionY, state.PositionZ);
            var rotation = new Quaternion(state.RotationX, state.RotationY, state.RotationZ, state.RotationW);
            node.Quaternion = rotation.LengthSquared() > 0.00001f ? rotation.Normalized() : Quaternion.Identity;
            RequireProvider(node).ApplyNetworkOwner(state.OwnerPeerId);
            node.Visible = state.Active;
        });

    public void Release(NetworkObjectInstance instance, NetworkObjectState state)
        => _thread.Invoke(() =>
        {
            var node = RequireNode(instance);
            bool rebound = instance.Identity.EntityId != 0 && instance.Identity.EntityId != state.EntityId;
            RequireProvider(node).ReleaseNetworkInstance(instance);
            Released++;
            if (rebound || !GodotObject.IsInstanceValid(node)) return;
            node.Visible = false;
            if (state.Kind != NetworkObjectKind.Scene)
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
        });

    public async UniTask SwitchToEngineThreadAsync(CancellationToken token)
        => await _thread.InvokeAsync(() => true, token);

    private static Node3D RequireNode(NetworkObjectInstance instance)
        => instance.Instance as Node3D ?? throw new ArgumentException("Expected a Godot Node3D.");
    private static IGodotNetworkObject RequireProvider(Node3D node)
        => node as IGodotNetworkObject ?? throw new ArgumentException("PackedScene root must implement IGodotNetworkObject.");
}
