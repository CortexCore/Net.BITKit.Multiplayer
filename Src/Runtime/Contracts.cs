using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{

public enum NetworkRole { Offline, Host, Client }
/// <summary>Logical, caller-assigned admission/session ID. Never reuse it for a replacement connection in the same room epoch.</summary>
public readonly struct PeerId : IEquatable<PeerId>
{
    public PeerId(string value) => Value = !string.IsNullOrWhiteSpace(value) && value.Length <= 128 ? value : throw new ArgumentException("Peer ID must be 1..128 characters");
    public string Value { get; }
    public bool Equals(PeerId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PeerId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public override string ToString() => Value ?? "";
}
public readonly struct RpcTarget
{
    public RpcTarget(PeerId peer) => Peer = peer;
    public PeerId Peer { get; }
}
public readonly struct TargetKey : IEquatable<TargetKey>
{
    public TargetKey(string service, string entity = "", string component = "")
    {
        Service = !string.IsNullOrWhiteSpace(service) && service.Length <= 256 ? service : throw new ArgumentException("Service key must be 1..256 characters");
        if (entity?.Length > 256 || component?.Length > 256) throw new ArgumentException("Entity/component key exceeds 256 characters");
        Entity = entity ?? ""; Component = component ?? "";
    }
    public string Service { get; }
    public string Entity { get; }
    public string Component { get; }
    public bool Equals(TargetKey other) => Service == other.Service && Entity == other.Entity && Component == other.Component;
    public override bool Equals(object? obj) => obj is TargetKey other && Equals(other);
    public override int GetHashCode() => (Service, Entity, Component).GetHashCode();
    public override string ToString() => Service + "/" + Entity + "/" + Component;
}
public interface INetworkContext
{
    NetworkRole Role { get; }
    bool IsHost { get; }
    bool IsClient { get; }
    PeerId LocalPeerId { get; }
    PeerId HostPeerId { get; }
    bool IsConnected { get; }
    bool IsReady { get; }
    string Scope { get; }
    long MemberVersion { get; }
    IReadOnlyList<RoomMember> Members { get; }
    bool TryGetMember(PeerId peer, out RoomMember? member);
    bool TryGetReadyPeerByPlayerId(string playerId, out PeerId peer);
    bool TryGetReadyPeerBySteamId(string steamId, out PeerId peer);
}
/// <summary>Read-only object ownership supplied by an authenticated entity adapter.
/// This is not RpcRuntime.Bind's private RPC/state recipient restriction.</summary>
public interface INetworkOwnership
{
    PeerId? OwnerPeerId { get; }
    event Action<PeerId?, PeerId?>? OwnershipChanged;
}
public sealed class RpcCallContext
{
    internal RpcCallContext(PeerId sender, PeerId executor, TargetKey target) { Sender = sender; Executor = executor; Target = target; }
    public PeerId Sender { get; }
    public PeerId Executor { get; }
    public TargetKey Target { get; }
    private static readonly AsyncLocal<RpcCallContext?> CurrentSlot = new();
    [ThreadStatic] private static RpcCallValueContext _value;
    private static bool ValueWins(RpcCallValueContext frame, RpcCallContext? current)
        => frame.Active && ReferenceEquals(frame.AsyncParent, current);
    public static RpcCallContext? Current
    {
        get
        {
            var current = CurrentSlot.Value;
            var frame = _value;
            return ValueWins(frame, current) ? new RpcCallContext(frame.Sender, frame.Executor, frame.Target) : current;
        }
    }
    public static bool TryGetValue(out RpcCallValueContext context)
    {
        var current = CurrentSlot.Value;
        var frame = _value;
        context = ValueWins(frame, current) ? frame : current != null
            ? new RpcCallValueContext(current.Sender, current.Executor, current.Target) : default;
        return context.Active;
    }
    internal static RpcCallValueContext EnterValue(PeerId sender, PeerId executor, TargetKey target)
    { var old = _value; _value = new RpcCallValueContext(sender, executor, target, CurrentSlot.Value); return old; }
    internal static void ExitValue(RpcCallValueContext before) => _value = before;
    internal static async Task<T> Run<T>(RpcCallContext context, Func<Task<T>> action)
    {
        var before = CurrentSlot.Value;
        CurrentSlot.Value = context;
        try { return await action().ConfigureAwait(false); }
        finally { CurrentSlot.Value = before; }
    }
}
public readonly struct RpcCallValueContext
{
    internal RpcCallValueContext(PeerId sender, PeerId executor, TargetKey target, RpcCallContext? asyncParent = null)
    { Sender = sender; Executor = executor; Target = target; AsyncParent = asyncParent; Active = true; }
    public PeerId Sender { get; }
    public PeerId Executor { get; }
    public TargetKey Target { get; }
    public bool Active { get; }
    internal RpcCallContext? AsyncParent { get; }
}
public enum RpcError { Unauthorized, MissingTarget, MissingMethod, InvalidPayload, RemoteFault, Timeout, Disconnected, Disposed, InvalidRole, LimitExceeded }
public sealed class RpcException : Exception
{
    public RpcException(RpcError error, string message) : base(message) => Error = error;
    public RpcError Error { get; }
}
public sealed class RoomMember
{
    public RoomMember(PeerId peer, string? playerId = null, string? steamId = null, bool ready = true)
    { Peer = peer; PlayerId = playerId; SteamId = steamId; Ready = ready; }
    public PeerId Peer { get; }
    public string? PlayerId { get; }
    public string? SteamId { get; }
    public bool Ready { get; }
}
public readonly struct AuthorizationRequest
{
    public AuthorizationRequest(RoomMember sender, PeerId executor, TargetKey target, string method)
    { Sender = sender; Executor = executor; Target = target; Method = method; }
    public RoomMember Sender { get; }
    public PeerId Executor { get; }
    public TargetKey Target { get; }
    public string Method { get; }
}
public interface IRoomWire : IDisposable
{
    bool IsConnected { get; }
    event Action<PeerId, byte[]>? Received;
    event Action<PeerId>? PeerLeft;
    Task SendAsync(PeerId peer, byte[] data);
}

// A versioned bounded envelope; method IDs are contract-signature strings, never CLR type names from the wire.
internal sealed class Packet
{
    public int V = 3;
    public string Scope = "";
    public string Kind = "";
    public string Id = "";
    public string Origin = "";
    public string Requester = "";
    public string Destination = "";
    public string Service = "";
    public string Entity = "";
    public string Component = "";
    public string Method = "";
    public SendTo To;
    public byte[][]? Args;
    public object?[]? BinaryArgs;
    public Type? ExpectedType;
    public byte[]? Result;
    public string? Fault;
    public RpcError Error;
    public bool OneWay;
    public string? Property;
    public long Version;
    public byte[]? Value;
    public List<MemberRecord>? Members;
    public List<TargetRecord>? RemovedTargets;
    public TargetKey Key => new(Service, Entity, Component);
    public Packet Copy() => (Packet)MemberwiseClone();
}

internal sealed class MemberRecord
{
    public string Peer = "";
    public string? PlayerId;
    public string? SteamId;
    public bool Ready;
}

internal sealed class TargetRecord
{
    public string Service = "";
    public string Entity = "";
    public string Component = "";
}
}
