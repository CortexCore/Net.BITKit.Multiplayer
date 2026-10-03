using BITKit.Multiplayer.TouchSocket;
using System.Security.Cryptography;
using System.Text;

namespace BITKit.Multiplayer.Samples.Arena;

/// <summary>Only a live relay room and its authenticated host may activate or close its lobby listing.</summary>
public sealed class ArenaRelayAuthorizer : IRelayRoomAuthorizer
{
    private readonly IArenaRelayLobbyApi _lobby;
    private static readonly TimeSpan AbandonedRegistrationLifetime = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registeredHosts = new(StringComparer.Ordinal);
    private sealed class Registration
    {
        public required byte[] Digest { get; init; }
        public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
        public bool Active { get; set; }
        public bool Closing { get; set; }
    }

    public ArenaRelayAuthorizer(IArenaRelayLobbyApi lobby) => _lobby = lobby;

    public async Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var room = await _lobby.ValidateRoomHostAsync(roomId, credential).WaitAsync(cancellationToken);
        if (room is not { ConnectionMode: ArenaConnectionMode.Relay } || room.RoomId != roomId ||
            string.IsNullOrWhiteSpace(room.Scope) || string.IsNullOrWhiteSpace(room.HostPeerId)) return null;
        // Re-registration with the same live credential is safe; the relay core enforces one
        // authenticated socket per room and may reject a first attempt for a scope mismatch.
        lock (_gate)
        {
            // Failed or abandoned registrations never accumulate indefinitely.
            foreach (var stale in _registeredHosts.Where(p => !p.Value.Active && !p.Value.Closing && DateTimeOffset.UtcNow - p.Value.Created >= AbandonedRegistrationLifetime).Select(p => p.Key).ToArray())
                _registeredHosts.Remove(stale);
            if (_registeredHosts.TryGetValue(roomId, out var registered))
            {
                if (registered.Closing || !Matches(registered.Digest, credential)) return null;
            }
            else _registeredHosts.Add(roomId, new Registration { Digest = Digest(credential) });
        }
        return new RelayRoomIdentity { Scope = room.Scope, HostPeerId = new PeerId(room.HostPeerId) };
    }

    public async Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Registered(roomId, credential)) return false;
        // Publish rejects expired rooms and incorrect credentials at the lobby authority.
        var published = await _lobby.PublishRelayRoomAsync(roomId, credential).WaitAsync(cancellationToken);
        if (published) lock (_gate) { if (_registeredHosts.TryGetValue(roomId, out var registered) && Matches(registered.Digest, credential)) registered.Active = true; }
        return published;
    }

    public async Task CloseRoomAsync(string roomId, string credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Registration registration;
        lock (_gate)
        {
            if (roomId is null || credential is null || !_registeredHosts.TryGetValue(roomId, out registration!) ||
                registration.Closing || !Matches(registration.Digest, credential)) return;
            // Claim this exact registered host before the remote close. Duplicate/stale callbacks
            // cannot remove another registration or issue another Lobby close.
            registration.Closing = true;
        }
        try { await _lobby.CloseRoomAsync(roomId, credential).WaitAsync(cancellationToken); }
        finally
        {
            lock (_gate)
                if (_registeredHosts.TryGetValue(roomId, out var current) && ReferenceEquals(current, registration))
                    _registeredHosts.Remove(roomId);
        }
    }

    private bool Registered(string roomId, string credential)
    {
        if (roomId is null || credential is null) return false;
        lock (_gate) return _registeredHosts.TryGetValue(roomId, out var registered) && !registered.Closing && Matches(registered.Digest, credential);
    }
    private static bool Matches(byte[] digest, string credential) => CryptographicOperations.FixedTimeEquals(digest, Digest(credential));
    private static byte[] Digest(string credential) => SHA256.HashData(Encoding.UTF8.GetBytes(credential));
}
