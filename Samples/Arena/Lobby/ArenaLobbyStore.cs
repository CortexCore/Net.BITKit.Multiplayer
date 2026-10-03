using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BITKit.Multiplayer.Samples.Arena;

/// <summary>Development-only lobby authority. All mutable state is protected by one gate.</summary>
public sealed class ArenaLobbyStore
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan RoomLifetime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(15);
    private const int MaxAccounts = 10000, MaxSessions = 2048, MaxRooms = 256, MaxTickets = 4096;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _data;
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Since, int Count)> _loginFailures = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _authWindow;
    private int _authWork;

    public ArenaLobbyStore(string? dataPath = null, Func<DateTimeOffset>? clock = null)
    {
        _data = dataPath;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        if (dataPath is not null && File.Exists(dataPath))
        {
            var loaded = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(dataPath)) ?? new();
            if (loaded.Count > MaxAccounts) throw new InvalidDataException("Account file exceeds capacity");
            foreach (var account in loaded)
            {
                if (!ValidUsername(account.Username) || !ValidKey(account.PlayerId) || account.Salt is not { Length: 16 } || account.Hash is not { Length: 32 } || !_accounts.TryAdd(account.Username, account))
                    throw new InvalidDataException("Invalid account file");
            }
        }
    }

    public Task<AuthSession> GuestAsync(string displayName)
    {
        lock (_gate)
        {
            Prune();
            if (displayName is null || displayName.Trim().Length is < 1 or > 32 || displayName.Any(char.IsControl)) return Task.FromResult(AuthFail("Invalid display name"));
            if (_sessions.Count >= MaxSessions) return Task.FromResult(AuthFail("Session capacity reached"));
            return Task.FromResult(NewSession(RandomId(), displayName.Trim()));
        }
    }

    public Task<AuthSession> RegisterAsync(string username, string password)
    {
        lock (_gate)
        {
            Prune();
            if (!ValidUsername(username) || !ValidPassword(password)) return Task.FromResult(AuthFail("Invalid username or password"));
            if (!AllowAuthWork()) return Task.FromResult(AuthFail("Try again later"));
            if (_accounts.ContainsKey(username)) return Task.FromResult(AuthFail("Username unavailable"));
            if (_accounts.Count >= MaxAccounts || _sessions.Count >= MaxSessions) return Task.FromResult(AuthFail("Capacity reached"));
            var salt = RandomNumberGenerator.GetBytes(16);
            var account = new Account { Username = username, PlayerId = RandomId(), Salt = salt, Hash = HashPassword(password, salt) };
            _accounts.Add(username, account);
            try { Persist(); }
            catch { _accounts.Remove(username); throw; }
            return Task.FromResult(NewSession(account.PlayerId, account.Username));
        }
    }

    public Task<AuthSession> LoginAsync(string username, string password)
    {
        lock (_gate)
        {
            Prune();
            if (!ValidUsername(username) || !ValidPassword(password)) return Task.FromResult(AuthFail("Invalid credentials"));
            if (!AllowAuthWork()) return Task.FromResult(AuthFail("Try again later"));
            if (_loginFailures.TryGetValue(username, out var attempts) && attempts.Count >= 5 && _now() - attempts.Since < TimeSpan.FromMinutes(1))
                return Task.FromResult(AuthFail("Try again later"));
            if (!_accounts.TryGetValue(username, out var account) || !CryptographicOperations.FixedTimeEquals(HashPassword(password, account.Salt), account.Hash))
            {
                if (_loginFailures.Count < 4096 || _loginFailures.ContainsKey(username))
                    _loginFailures[username] = (attempts.Count == 0 || _now() - attempts.Since >= TimeSpan.FromMinutes(1) ? _now() : attempts.Since,
                        attempts.Count == 0 || _now() - attempts.Since >= TimeSpan.FromMinutes(1) ? 1 : attempts.Count + 1);
                return Task.FromResult(AuthFail("Invalid credentials"));
            }
            _loginFailures.Remove(username);
            if (_sessions.Count >= MaxSessions) return Task.FromResult(AuthFail("Session capacity reached"));
            return Task.FromResult(NewSession(account.PlayerId, account.Username));
        }
    }

    public Task<RoomRegistration> CreateRoomAsync(string sessionToken, string name, string address, int port)
    {
        lock (_gate)
        {
            Prune();
            if (!HasSession(sessionToken)) return Task.FromResult(new RoomRegistration { Error = "Unauthorized" });
            if (name is null || name.Trim().Length is < 1 or > 64 || name.Any(char.IsControl) ||
                address is null || address.Length is < 1 or > 253 || address.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')) ||
                port is < 1 or > 65535) return Task.FromResult(new RoomRegistration { Error = "Invalid room details" });
            if (_rooms.Count >= MaxRooms) return Task.FromResult(new RoomRegistration { Error = "Room capacity reached" });
            return Task.FromResult(AddRoom(name.Trim(), address, port, ArenaConnectionMode.Direct, false));
        }
    }

    public Task<RoomRegistration> ReserveRelayRoomAsync(string sessionToken, string name, string relayAddress, int relayPort, bool useTls)
    {
        lock (_gate)
        {
            Prune();
            if (!HasSession(sessionToken)) return Task.FromResult(new RoomRegistration { Error = "Unauthorized" });
            if (name is null || name.Trim().Length is < 1 or > 64 || name.Any(char.IsControl) ||
                relayAddress is null || relayAddress.Length is < 1 or > 253 || relayAddress.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')) ||
                relayPort is < 1 or > 65535) return Task.FromResult(new RoomRegistration { Error = "Invalid room details" });
            if (_rooms.Count >= MaxRooms) return Task.FromResult(new RoomRegistration { Error = "Room capacity reached" });
            return Task.FromResult(AddRoom(name.Trim(), relayAddress, relayPort, ArenaConnectionMode.Relay, useTls));
        }
    }

    public Task<RoomInfo?> ValidateRoomHostAsync(string roomId, string hostToken)
    {
        lock (_gate)
        {
            Prune();
            return Task.FromResult(ValidKey(roomId) && ValidKey(hostToken) && _rooms.TryGetValue(roomId, out var room) && Matches(room.HostDigest, hostToken)
                ? Copy(room.Info) : null);
        }
    }

    public Task<bool> PublishRelayRoomAsync(string roomId, string hostToken)
    {
        lock (_gate)
        {
            Prune();
            if (!ValidKey(roomId) || !ValidKey(hostToken) || !_rooms.TryGetValue(roomId, out var room) ||
                room.Info.ConnectionMode != ArenaConnectionMode.Relay || !Matches(room.HostDigest, hostToken)) return Task.FromResult(false);
            room.Published = true;
            return Task.FromResult(true);
        }
    }

    private RoomRegistration AddRoom(string name, string address, int port, ArenaConnectionMode mode, bool useTls)
    {
        var id = RandomId();
        var info = new RoomInfo { RoomId = id, Name = name, Address = address, Port = port, Scope = RandomId(), HostPeerId = RandomId(),
            ConnectionMode = mode, UseTls = useTls, ExpiresUnixMilliseconds = (_now() + RoomLifetime).ToUnixTimeMilliseconds() };
        var hostToken = RandomId();
        _rooms.Add(id, new Room(info, Digest(hostToken), mode == ArenaConnectionMode.Direct));
        return new RoomRegistration { Success = true, Room = Copy(info), HostToken = hostToken };
    }

    public Task<RoomInfo[]> ListRoomsAsync(string sessionToken)
    {
        lock (_gate) { Prune(); return Task.FromResult(HasSession(sessionToken) ? _rooms.Values.Where(r => r.Published).Select(r => Copy(r.Info)).ToArray() : Array.Empty<RoomInfo>()); }
    }

    public Task<JoinTicket> JoinRoomAsync(string sessionToken, string roomId)
    {
        lock (_gate)
        {
            Prune();
            if (!TrySession(sessionToken, out var session)) return Task.FromResult(new JoinTicket { Error = "Unauthorized" });
            if (!ValidKey(roomId) || !_rooms.TryGetValue(roomId, out var room) || !room.Published) return Task.FromResult(new JoinTicket { Error = "Room unavailable" });
            if (_tickets.Count >= MaxTickets) return Task.FromResult(new JoinTicket { Error = "Ticket capacity reached" });
            var value = RandomId();
            _tickets.Add(Digest(value), new Ticket(roomId, session.PlayerId, session.DisplayName, _now() + TicketLifetime));
            return Task.FromResult(new JoinTicket { Success = true, Ticket = value, Room = Copy(room.Info) });
        }
    }

    public Task<AdmissionIdentity> RedeemTicketAsync(string roomId, string hostToken, string ticket)
    {
        lock (_gate)
        {
            Prune();
            // Only consume after *all* checks pass; invalid host/room attempts must leave the ticket untouched.
            if (!ValidKey(roomId) || !ValidKey(hostToken) || !ValidKey(ticket) || !_rooms.TryGetValue(roomId, out var room) || !Matches(room.HostDigest, hostToken) ||
                !_tickets.TryGetValue(Digest(ticket), out var admission) || admission.RoomId != roomId)
                return Task.FromResult(new AdmissionIdentity { Error = "Invalid or expired admission" });
            _tickets.Remove(Digest(ticket));
            return Task.FromResult(new AdmissionIdentity { Success = true, PlayerId = admission.PlayerId, DisplayName = admission.DisplayName, PeerId = RandomId() });
        }
    }

    public Task<bool> HeartbeatRoomAsync(string roomId, string hostToken)
    {
        lock (_gate)
        {
            Prune();
            if (!ValidKey(roomId) || !ValidKey(hostToken) || !_rooms.TryGetValue(roomId, out var room) || !Matches(room.HostDigest, hostToken)) return Task.FromResult(false);
            room.Info.ExpiresUnixMilliseconds = (_now() + RoomLifetime).ToUnixTimeMilliseconds();
            return Task.FromResult(true);
        }
    }

    public Task<bool> CloseRoomAsync(string roomId, string hostToken)
    {
        lock (_gate)
        {
            Prune();
            if (!ValidKey(roomId) || !ValidKey(hostToken) || !_rooms.TryGetValue(roomId, out var room) || !Matches(room.HostDigest, hostToken)) return Task.FromResult(false);
            _rooms.Remove(roomId);
            RemoveRoomTickets(roomId);
            return Task.FromResult(true);
        }
    }

    private void Prune()
    {
        var now = _now();
        foreach (var key in _sessions.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _sessions.Remove(key);
        foreach (var key in _rooms.Where(p => p.Value.Info.ExpiresUnixMilliseconds <= now.ToUnixTimeMilliseconds()).Select(p => p.Key).ToArray()) { _rooms.Remove(key); RemoveRoomTickets(key); }
        foreach (var key in _tickets.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _tickets.Remove(key);
        foreach (var key in _loginFailures.Where(p => now - p.Value.Since >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _loginFailures.Remove(key);
    }
    private void RemoveRoomTickets(string roomId) { foreach (var key in _tickets.Where(p => p.Value.RoomId == roomId).Select(p => p.Key).ToArray()) _tickets.Remove(key); }
    private bool HasSession(string? token) => TrySession(token, out _);
    private bool TrySession(string? token, out Session session)
    {
        if (ValidKey(token)) return _sessions.TryGetValue(Digest(token), out session!);
        session = null!;
        return false;
    }
    private bool AllowAuthWork()
    {
        if (_now() - _authWindow >= TimeSpan.FromMinutes(1)) { _authWindow = _now(); _authWork = 0; }
        return ++_authWork <= 120;
    }
    private AuthSession NewSession(string playerId, string displayName)
    {
        var token = RandomId();
        _sessions.Add(Digest(token), new Session(playerId, displayName, _now() + SessionLifetime));
        return new AuthSession { Success = true, PlayerId = playerId, DisplayName = displayName, SessionToken = token };
    }
    private void Persist()
    {
        if (_data is null) return;
        var path = Path.GetFullPath(_data);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_accounts.Values.ToArray()));
        File.Move(temporary, path, true);
    }
    private static bool ValidUsername(string? s) => s is { Length: >= 3 and <= 32 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool ValidPassword(string? s) => s is { Length: >= 8 and <= 128 };
    private static bool ValidKey(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32);
    private static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Digest(string? value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
    private static bool Matches(string digest, string value) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AuthSession AuthFail(string error) => new() { Error = error };
    private static RoomInfo Copy(RoomInfo i) => new() { RoomId = i.RoomId, Name = i.Name, Address = i.Address, Port = i.Port, Scope = i.Scope, HostPeerId = i.HostPeerId, ExpiresUnixMilliseconds = i.ExpiresUnixMilliseconds, ConnectionMode = i.ConnectionMode, UseTls = i.UseTls };
    public sealed class Account { public string Username { get; set; } = ""; public string PlayerId { get; set; } = ""; public byte[] Salt { get; set; } = Array.Empty<byte>(); public byte[] Hash { get; set; } = Array.Empty<byte>(); }
    private sealed record Session(string PlayerId, string DisplayName, DateTimeOffset Expires);
    private sealed record Ticket(string RoomId, string PlayerId, string DisplayName, DateTimeOffset Expires);
    private sealed class Room
    {
        public Room(RoomInfo info, string hostDigest, bool published) { Info = info; HostDigest = hostDigest; Published = published; }
        public RoomInfo Info { get; }
        public string HostDigest { get; }
        public bool Published { get; set; }
    }
}
