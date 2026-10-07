using BITKit.Multiplayer.NetRpc;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>Node3D scene roots implement this local provider boundary; components remain pure Core types.</summary>
public interface IGodotNetworkObject : INetworkIdentity
{
    NetworkObjectInstance CreateNetworkInstance();
    void BindNetworkIdentity(uint entityId, uint ownerPeerId, bool authority);
    void ApplyNetworkOwner(uint ownerPeerId);
    void ReleaseNetworkInstance(NetworkObjectInstance instance);
}
