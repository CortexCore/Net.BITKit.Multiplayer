using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Reflection;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;

namespace BITKit.Multiplayer.Samples.Arena;

public sealed class PlayerHealth
{
    [SyncVar] public int Health { get; private set; } = 100;
    public void SetHealth(int health) => Health = health;
}

public sealed class ArenaGameService
{
    private readonly ArenaSessionImpl _session;
    public ArenaGameService(ArenaSessionImpl session) => _session = session;
    [Rpc(SendTo.Host)] public void SubmitMove(float x, float z, uint sequence)
    {
        if (!RpcCallContext.TryGetValue(out RpcCallValueContext context))
            throw new InvalidOperationException("SubmitMove requires an authenticated RPC sender");
        _session.Authority!.Move(context.Sender.Value, x, z, sequence);
    }
    [Rpc(SendTo.Host)] public void SubmitShot(float x, float z, uint sequence)
    {
        if (!RpcCallContext.TryGetValue(out RpcCallValueContext context))
            throw new InvalidOperationException("SubmitShot requires an authenticated RPC sender");
        var b = _session.Authority!.Fire(context.Sender.Value, x, z, sequence);
        if (b != null) { PublishBullet(b, false); PublishScore(_session.Authority.Shots, _session.Authority.Hits); }
    }
    [Rpc(SendTo.Host)] public Task<RoomSnapshot> RequestSnapshot()
        => Task.FromResult(_session.Authority!.Snapshot(_session.Scope));
    [Rpc(SendTo.Host)] public Task<long> QueryHostTick() => Task.FromResult(_session.Authority!.TickNumber);
    [Rpc(SendTo.All)] public void PublishRoster(RoomSnapshot snapshot) => _session.ReceiveRoster(snapshot);
    [Rpc(SendTo.All)] public void PublishScore(int shots, int hits) => _session.ReceiveScore(shots, hits);
    [Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)] public void PublishPlayerMotion(long tick, ArraySegment<PlayerMotionUpdate> updates)
        => _session.ReceivePlayerMotion(tick, updates);
    [Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)] public void PublishBulletMotion(long tick, ArraySegment<BulletMotionUpdate> updates)
        => _session.ReceiveBulletMotion(tick, updates);
    [Rpc(SendTo.All)] public void PublishBullet(BulletPose bullet, bool ended)
        => _session.ReceiveBullet(bullet, ended);
}

public static class ArenaSession
{
    public static async Task<IArenaSession> StartHostAsync(IArenaLobbyApi lobby, AuthSession auth, ArenaHostOptions options, CancellationToken token = default, ITransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(lobby); ArgumentNullException.ThrowIfNull(auth); ArgumentNullException.ThrowIfNull(options);
        if (!auth.Success || string.IsNullOrWhiteSpace(auth.SessionToken) || string.IsNullOrWhiteSpace(auth.PlayerId)) throw new ArgumentException("Authenticated host required");
        var session = new ArenaSessionImpl(lobby, true, transportFactory);
        try { await session.StartHost(auth, options, token); return session; }
        catch { await session.DisposeAsync(); throw; }
    }
    public static async Task<IArenaSession> StartClientAsync(IArenaLobbyApi lobby, AuthSession auth, ArenaClientOptions options, CancellationToken token = default, ITransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(lobby); ArgumentNullException.ThrowIfNull(auth); ArgumentNullException.ThrowIfNull(options);
        if (!auth.Success || string.IsNullOrWhiteSpace(auth.SessionToken) || string.IsNullOrWhiteSpace(auth.PlayerId)) throw new ArgumentException("Authenticated client required");
        var session = new ArenaSessionImpl(lobby, false, transportFactory);
        try { await session.StartClient(auth, options, token); return session; }
        catch { await session.DisposeAsync(); throw; }
    }
}

// The Host registers a provisional member before Relay has finished admitting its socket.
// Its first directory notification is retried by the Client's members request after admission.
// Drop only that provisional peer's unavailable send; all other wire failures still surface.
internal sealed class ArenaRelayHostRuntimeWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
{
    private readonly RelayHostWire _wire;
    private readonly IRoomMemoryWire _memory;
    private readonly IRoomDatagrams _udp;
    private readonly Func<PeerId, bool> _isProvisional;
    public ArenaRelayHostRuntimeWire(RelayHostWire wire, Func<PeerId, bool> isProvisional)
    {
        _wire = wire; _memory = wire; _udp = wire; _isProvisional = isProvisional;
        _wire.Received += (peer, data) => Received?.Invoke(peer, data);
        _memory.MemoryReceived += (peer, data) => MemoryReceived?.Invoke(peer, data);
        _wire.PeerLeft += peer => PeerLeft?.Invoke(peer);
        _udp.UnreliableReceived += (peer, data) => UnreliableReceived?.Invoke(peer, data);
    }
    public bool IsConnected => _wire.IsConnected;
    public event Action<PeerId, byte[]>? Received;
    public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
    public event Action<PeerId>? PeerLeft;
    public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
    public int MaxUnreliablePayloadBytes => _udp.MaxUnreliablePayloadBytes;
    public bool UnreliableEnabled { get => _udp.UnreliableEnabled; set => _udp.UnreliableEnabled = value; }
    public bool IsUnreliableReady(PeerId peer) => _udp.IsUnreliableReady(peer);
    public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) => _udp.SendUnreliableAsync(peer, payload);
    public DatagramStatistics GetDatagramStatistics() => _udp.GetDatagramStatistics();
    public Task RebindDatagramsAsync(CancellationToken token = default) => _udp.RebindDatagramsAsync(token);
    public async Task SendAsync(PeerId peer, byte[] data)
    {
        try { await _wire.SendAsync(peer, data); }
        catch (RpcException ex) when (ex.Error == RpcError.MissingTarget &&
                                      ex.Message == "Relay peer unavailable" && _isProvisional(peer)) { }
    }
    public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
    {
        try
        {
            ValueTask send = _memory.SendMemoryAsync(peer, payload);
            if (!send.IsCompletedSuccessfully) return AwaitSend(send, peer);
            send.GetAwaiter().GetResult(); // Consume even a synchronously completed ValueTask.
            return ValueTask.CompletedTask;
        }
        catch (RpcException ex) when (ex.Error == RpcError.MissingTarget &&
                                      ex.Message == "Relay peer unavailable" && _isProvisional(peer))
        { return ValueTask.CompletedTask; }
    }
    private async ValueTask AwaitSend(ValueTask send, PeerId peer)
    {
        try { await send.ConfigureAwait(false); }
        catch (RpcException ex) when (ex.Error == RpcError.MissingTarget &&
                                      ex.Message == "Relay peer unavailable" && _isProvisional(peer)) { }
    }
    public void Dispose() { /* ArenaSessionImpl owns the physical wire. */ }
}

public sealed class ArenaSessionImpl : IArenaSession
{
    private static readonly TargetKey ServiceKey = new("arena.game");
    private readonly IArenaLobbyApi _lobby;
    private readonly ITransportFactory _transportFactory;
    private readonly object _gate = new();
    // Binding lifecycle never calls RpcRuntime while holding _gate. Its callbacks may re-enter the session.
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _routeLost = new();
    private readonly Dictionary<string, (PlayerHealth State, TargetKey Key)> _health = new();
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> _playerTicks = new();
    private readonly Dictionary<long, long> _bulletTicks = new();
    private readonly PlayerMotionUpdate[] _playerBuffer = new PlayerMotionUpdate[64];
    private readonly BulletMotionUpdate[] _bulletBuffer = new BulletMotionUpdate[256];
    private readonly object?[] _measureArgs = new object?[2];
    private static readonly MethodInfo PlayerMotionMethod = typeof(ArenaGameService).GetMethod(nameof(ArenaGameService.PublishPlayerMotion))!;
    private static readonly MethodInfo BulletMotionMethod = typeof(ArenaGameService).GetMethod(nameof(ArenaGameService.PublishBulletMotion))!;
    private long _playerBatches, _bulletBatches, _positionTick, _probes, _healthUpdates, _sendBytes;
    private long _lastRosterTick = -1;
    private ArenaAllocationReport? _allocationPhases;
    public void SetAllocationPhases(ArenaAllocationReport? report) => Volatile.Write(ref _allocationPhases, report);
    private IRoomDatagrams? _datagrams;
    private readonly bool _host;
    private IRoomWire? _hostWire;
    private IRoomWire? _clientWire;
    private Func<Task>? _notifyLeaving;
    private RpcRuntime? _runtime;
    private ArenaGameService? _service;
    private Task? _tickTask, _leaseTask;
    private Task? _probeTask;
    private RoomSnapshot _view = new();
    private string _hostToken = "";
    private int _disposed;
    private int _failed;
    public ArenaAuthority? Authority { get; private set; }
    public string Scope { get; private set; } = "";
    public bool IsHost => _host;
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failed) == 0 &&
        (_host ? _hostWire : _clientWire)?.IsConnected == true && _runtime?.IsReady == true;
    public string LocalPlayerId { get; private set; } = "";
    public string LocalPeerId { get; private set; } = "";
    public string RoomId { get; private set; } = "";
    public string RoomName { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public ArenaConnectionMode ConnectionMode { get; private set; } = ArenaConnectionMode.Direct;
    public string ConnectedEndpoint { get; private set; } = "";
    public string TransportName => _transportFactory.Name;
    public ArenaSessionImpl(IArenaLobbyApi lobby, bool host, ITransportFactory? transportFactory = null)
    { _lobby = lobby; _host = host; _transportFactory = transportFactory ?? new UdpTransportFactory(); }
    private static TargetKey HealthKey(string peer) => new("arena.health", peer);
    private void BindHealth(string peer)
    {
        lock (_lifecycle)
        {
            lock (_gate) if (_retired.Contains(peer) || _health.ContainsKey(peer)) return;
            var key = HealthKey(peer); var state = new PlayerHealth();
            _runtime!.Bind(key, state, _host ? request => request.Sender.Ready : null);
            lock (_gate) _health.Add(peer, (state, key));
        }
    }
    private void RemoveHealth(string peer)
    {
        lock (_lifecycle)
        {
            (PlayerHealth State, TargetKey Key) state;
            lock (_gate) { _retired.Add(peer); if (!_health.Remove(peer, out state)) return; }
            _runtime!.RemoveTarget(state.Key);
        }
    }
    private void MembersChanged(long _)
    {
        if (Authority == null && _host || _runtime == null) return;
        var connected = _runtime.Members.Select(m => m.Peer.Value).ToHashSet();
        if (_host)
        {
            bool changed = false;
            string[] peers;
            lock (Authority!.Gate) peers = Authority.Players.Keys.Where(p => !connected.Contains(p)).ToArray();
            foreach (var peer in peers) { Authority.Remove(peer); RemoveHealth(peer); changed = true; }
            if (changed) _service?.PublishRoster(Authority.Snapshot(Scope));
        }
        else
        {
            string[] removed;
            lock (_gate) removed = _health.Keys.Where(p => !connected.Contains(p)).ToArray();
            foreach (var peer in removed) RemoveHealth(peer);
            lock (_gate)
            {
                _view.Players = _view.Players.Where(p => connected.Contains(p.PeerId)).ToArray();
                PruneMotionTicks();
            }
        }
    }
    public async Task StartHost(AuthSession auth, ArenaHostOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.ConnectionMode is not (ArenaConnectionMode.Direct or ArenaConnectionMode.Relay))
            throw new ArgumentOutOfRangeException(nameof(options.ConnectionMode));
        bool relay = options.ConnectionMode == ArenaConnectionMode.Relay;
        if (relay && _lobby is not IArenaRelayLobbyApi)
            throw new InvalidOperationException("Relay host requires a relay-capable lobby");
        var directWire = relay ? null : new TouchSocketHostWire(_transportFactory);
        var relayWire = relay ? new RelayHostWire(_transportFactory) : null;
        IRoomWire wire = _hostWire = (IRoomWire?)relayWire ?? directWire!;
        var pending = new ConcurrentDictionary<string, AdmissionIdentity>();
        var admissionGate = new object();
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var provisional = new HashSet<string>(StringComparer.Ordinal);
        var rejectedDuplicates = new Dictionary<string, int>(StringComparer.Ordinal);
        async Task<(PeerId Peer, string Response)> Admit(string ticket)
        {
            if (ticket.Length > 4096) throw new ArgumentException("Ticket too large");
            var identity = await _lobby.RedeemTicketAsync(RoomId, _hostToken, ticket);
            if (!identity.Success || string.IsNullOrWhiteSpace(identity.PeerId) || string.IsNullOrWhiteSpace(identity.PlayerId)) throw new InvalidOperationException("Ticket denied");
            var peer = new PeerId(identity.PeerId);
            var response = JsonSerializer.Serialize(identity);
            if (System.Text.Encoding.UTF8.GetByteCount(response) > 4096 || pending.Count >= 256) throw new InvalidOperationException("Admission limit exceeded");
            if (!pending.TryAdd(identity.PeerId, identity)) throw new InvalidOperationException("Duplicate peer");
            return (peer, response);
        }
        void Admitted(PeerId peer)
        {
            lock (admissionGate)
            {
                if (!pending.TryRemove(peer.Value, out var identity)) throw new InvalidOperationException("Duplicate peer");
                if (committed.Contains(peer.Value) || !Authority!.Add(identity.PlayerId, peer.Value, identity.DisplayName))
                {
                    // The ensuing aborted callback belongs to this rejected attempt, not to the
                    // earlier live peer using the same ID.
                    rejectedDuplicates[peer.Value] = rejectedDuplicates.GetValueOrDefault(peer.Value) + 1;
                    throw new InvalidOperationException("Duplicate player");
                }
                committed.Add(peer.Value);
                if (relay) provisional.Add(peer.Value);
                try
                {
                    _runtime!.HoldBroadcasts(peer);
                    _runtime.RegisterMember(new RoomMember(peer, identity.PlayerId, ready: false));
                    BindHealth(peer.Value);
                }
                catch
                {
                    committed.Remove(peer.Value);
                    provisional.Remove(peer.Value);
                    _runtime?.RemoveMember(peer);
                    Authority.Remove(peer.Value);
                    RemoveHealth(peer.Value);
                    throw;
                }
            }
        }
        void Aborted(PeerId peer)
        {
            lock (admissionGate)
            {
                if (rejectedDuplicates.TryGetValue(peer.Value, out var ignored))
                {
                    if (ignored == 1) rejectedDuplicates.Remove(peer.Value);
                    else rejectedDuplicates[peer.Value] = ignored - 1;
                    return;
                }
                if (pending.TryRemove(peer.Value, out _) || !committed.Remove(peer.Value)) return;
                // Relay may reject or lose a socket after Admitted but before the client is
                // accepted. Retire precisely that provisional member and its woven SyncVar.
                _runtime?.RemoveMember(peer);
                Authority?.Remove(peer.Value);
                RemoveHealth(peer.Value);
                if (_service != null && Authority != null) _service.PublishRoster(Authority.Snapshot(Scope));
            }
        }
        void PeerLeft(PeerId peer) { lock (admissionGate) { committed.Remove(peer.Value); provisional.Remove(peer.Value); } }
        wire.PeerLeft += PeerLeft;
        void Prepared(PeerId peer)
        {
            lock (admissionGate) provisional.Remove(peer.Value);
            var member = _runtime!.Members.FirstOrDefault(m => m.Peer.Equals(peer));
            if (member?.PlayerId != null && !member.Ready)
            {
                _runtime.RegisterMember(new RoomMember(peer, member.PlayerId));
                _service?.PublishRoster(Authority!.Snapshot(Scope));
            }
        }
         void DirectoryReady(PeerId peer) => _runtime!.ReleaseBroadcasts(peer);
        // Direct listens before publication. Relay reserves a hidden room before dialing outbound;
        // activation is deliberately deferred until the authority, runtime and bindings exist.
        if (directWire != null)
            await directWire.StartAsync(options.Port, ArenaProtocol.GameToken, IPAddress.Any, Admit, Admitted, Aborted);
        var registration = relay
            ? await ((IArenaRelayLobbyApi)_lobby).ReserveRelayRoomAsync(auth.SessionToken, options.RoomName, options.RelayAddress, options.RelayPort, options.RelayUseTls)
            : await _lobby.CreateRoomAsync(auth.SessionToken, options.RoomName, options.AdvertisedAddress, options.Port);
        if (!registration.Success) throw new InvalidOperationException(registration.Error);
        RoomId = registration.Room.RoomId; RoomName = registration.Room.Name; Scope = registration.Room.Scope;
        _hostToken = registration.HostToken; LocalPeerId = registration.Room.HostPeerId;
        if (!relay && registration.Room.ConnectionMode != ArenaConnectionMode.Direct)
            throw new InvalidOperationException("Direct registration returned a different route");
        if (relay && (registration.Room.ConnectionMode != ArenaConnectionMode.Relay ||
                      registration.Room.Address != options.RelayAddress || registration.Room.Port != options.RelayPort ||
                      registration.Room.UseTls != options.RelayUseTls))
            throw new InvalidOperationException("Relay reservation does not match requested endpoint");
        if (relayWire != null)
            await relayWire.ConnectAsync(new RelayConnectOptions { Address = registration.Room.Address,
                Port = registration.Room.Port, VerifyToken = ArenaProtocol.RelayToken,
                RoomId = RoomId, Scope = Scope, HostPeerId = new PeerId(LocalPeerId),
                HostCredential = _hostToken, UseTls = registration.Room.UseTls,
                TlsTargetHost = registration.Room.Address }, Admit, Admitted, Aborted, token);
        if (directWire != null) { directWire.PeerPrepared += Prepared; directWire.PeerDirectoryReady += DirectoryReady; }
        if (relayWire != null) { relayWire.PeerPrepared += Prepared; relayWire.PeerDirectoryReady += DirectoryReady; }
        Authority = new ArenaAuthority();
        var runtimeWire = relayWire == null ? wire : new ArenaRelayHostRuntimeWire(relayWire, peer =>
        { lock (admissionGate) return provisional.Contains(peer.Value); });
        _runtime = new RpcRuntime(NetworkRole.Host, Scope, new PeerId(LocalPeerId), new PeerId(LocalPeerId), runtimeWire);
        _datagrams = runtimeWire as IRoomDatagrams ?? throw new InvalidOperationException("UDP room lane unavailable");
        _runtime.MembersChanged += MembersChanged;
        _runtime.UnhandledDispatch += ex => LastError = ex.Message;
        void AdmissionFailed(string reason) { if (reason == "Admission timed out") LastError = reason; }
        if (directWire != null) directWire.AdmissionFailed += AdmissionFailed;
        if (relayWire != null) relayWire.AdmissionFailed += AdmissionFailed;
        _service = new ArenaGameService(this);
        _runtime.Bind(ServiceKey, _service, request => request.Sender.Ready);
        if (options.LocalPlayer)
        {
            LocalPlayerId = auth.PlayerId;
            Authority.Add(auth.PlayerId, LocalPeerId, auth.DisplayName);
            BindHealth(LocalPeerId);
        }
        token.ThrowIfCancellationRequested();
        if (relayWire != null) await relayWire.ActivateAsync(token);
        ConnectionMode = relay ? ArenaConnectionMode.Relay : ArenaConnectionMode.Direct;
        ConnectedEndpoint = Endpoint(registration.Room.Address, registration.Room.Port);
        _tickTask = RunTicks(_stop.Token);
        _leaseTask = RunLease(_stop.Token);
    }
    public async Task StartClient(AuthSession auth, ArenaClientOptions options, CancellationToken token)
    {
        var phase = "room listing";
        try
        {
        var rooms = await _lobby.ListRoomsAsync(auth.SessionToken);
        var room = rooms.FirstOrDefault(r => string.IsNullOrEmpty(options.RoomId) || r.RoomId == options.RoomId)
            ?? throw new InvalidOperationException("Room not found");
        phase = "join ticket";
        var ticket = await _lobby.JoinRoomAsync(auth.SessionToken, room.RoomId);
        if (!ticket.Success) throw new InvalidOperationException(ticket.Error);
        room = ticket.Room;
        Scope = room.Scope; RoomId = room.RoomId; RoomName = room.Name;
        if (room.ConnectionMode is not (ArenaConnectionMode.Direct or ArenaConnectionMode.Relay))
            throw new InvalidOperationException("Unsupported room connection mode");
        var directWire = room.ConnectionMode == ArenaConnectionMode.Direct ? new TouchSocketClientWire(new PeerId(room.HostPeerId), _transportFactory) : null;
        var relayWire = room.ConnectionMode == ArenaConnectionMode.Relay ? new RelayClientWire(new PeerId(room.HostPeerId), _transportFactory) : null;
        IRoomWire wire = _clientWire = (IRoomWire?)relayWire ?? directWire!;
        wire.PeerLeft += _ => { try { _routeLost.Cancel(); } catch (ObjectDisposedException) { } };
        phase = "transport connect";
        if (directWire != null) await directWire.ConnectAsync(room.Address, room.Port, ArenaProtocol.GameToken);
        else await relayWire!.ConnectAsync(new RelayConnectOptions { Address = room.Address, Port = room.Port,
            VerifyToken = ArenaProtocol.RelayToken, RoomId = room.RoomId, Scope = room.Scope,
            HostPeerId = new PeerId(room.HostPeerId), UseTls = room.UseTls, TlsTargetHost = room.Address }, token);
        ConnectionMode = room.ConnectionMode;
        ConnectedEndpoint = Endpoint(room.Address, room.Port);
        Task<string> RequestAdmission(string value) => directWire != null ? directWire.RequestAdmissionAsync(value, token) : relayWire!.RequestAdmissionAsync(value, token);
        Task NotifyPrepared() => directWire != null ? directWire.NotifyPreparedAsync() : relayWire!.NotifyPreparedAsync();
        Task NotifyDirectoryReady() => directWire != null ? directWire.NotifyDirectoryReadyAsync() : relayWire!.NotifyDirectoryReadyAsync();
        _notifyLeaving = () => directWire != null ? directWire.NotifyLeavingAsync() : relayWire!.NotifyLeavingAsync();
        phase = "socket admission";
        var identity = JsonSerializer.Deserialize<AdmissionIdentity>(await RequestAdmission(ticket.Ticket))
            ?? throw new InvalidOperationException("Admission rejected");
        if (!identity.Success || identity.PlayerId != auth.PlayerId || string.IsNullOrWhiteSpace(identity.PeerId)) throw new InvalidOperationException("Admission rejected");
        LocalPlayerId = identity.PlayerId; LocalPeerId = identity.PeerId;
        phase = "runtime directory";
        _runtime = new RpcRuntime(NetworkRole.Client, Scope, new PeerId(LocalPeerId), new PeerId(room.HostPeerId), wire);
        _datagrams = wire as IRoomDatagrams ?? throw new InvalidOperationException("UDP room lane unavailable");
        _runtime.StateChanged += (key, property) => { if (key.Service == "arena.health" && property == nameof(PlayerHealth.Health)) Interlocked.Increment(ref _healthUpdates); };
        _runtime.UnhandledDispatch += ex => LastError = ex.Message;
        _runtime.MembersChanged += MembersChanged;
        _service = new ArenaGameService(this);
        _runtime.Bind(ServiceKey, _service);
        _runtime.ConfirmReady();
        await NotifyPrepared();
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token, _routeLost.Token);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!_runtime.IsReady) { await Task.Delay(20, readyTimeout.Token); if (!wire.IsConnected) throw new InvalidOperationException("Host disconnected"); }
        phase = "UDP binding";
        while (!_datagrams.IsUnreliableReady(new PeerId(room.HostPeerId)))
        {
            if (!wire.IsConnected) throw new InvalidOperationException("Host disconnected before UDP binding");
            try { await Task.Delay(20, readyTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_routeLost.IsCancellationRequested)
            { throw new TimeoutException("UDP binding did not become ready within 8 seconds"); }
        }
        await NotifyDirectoryReady();
        phase = "initial snapshot";
        var snapshot = await _service.RequestSnapshot().WaitAsync(readyTimeout.Token);
        ApplySnapshot(snapshot);
        // Initial pose discovery can precede the corresponding SyncVar state packet. Never seed HUD
        // health from a snapshot: wait for non-default health to arrive through the woven binding.
        while (!HealthInitialized()) { if (!wire.IsConnected) throw new InvalidOperationException("Host disconnected"); await Task.Delay(20, readyTimeout.Token); }
        _probeTask = RunProbes(_stop.Token);
        }
        catch (RpcException ex)
        {
            // Error category and phase contain no account or ticket data; wire exception messages may.
            var detail = ex.Error == RpcError.Disposed && ex.Message is "Binding changed during invocation" or "Target removed" or "Target unbound" or "Unbound object" or "Scope disposed" or "Unbound state" or "Detached state object" or "Target identity permanently removed; use a new key or scope"
                ? " (" + ex.Message + ")" : "";
            Console.Error.WriteLine($"Arena client {phase}: {ex.Error}{detail}");
            throw;
        }
        catch (TimeoutException) when (phase == "UDP binding")
        {
            Console.Error.WriteLine("Arena client UDP binding: timed out");
            throw;
        }
    }
    private bool HealthInitialized()
    {
        lock (_gate) return _view.Players.All(p => _health.TryGetValue(p.PeerId, out var state) && (p.Health == 100 || state.State.Health != 100));
    }
    private async Task RunTicks(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_hostWire?.IsConnected != true) { FailHost("Game route disconnected"); break; }
                bool lifeChanged = false;
                int previousHits = Authority!.Hits;
                var phases = Volatile.Read(ref _allocationPhases);
                var interval = phases?.Start(ArenaAllocationPhase.AuthorityStep) ?? default;
                var ended = Authority!.Step((peer, hp) => { PlayerHealth? state; lock (_gate) state = _health.TryGetValue(peer, out var entry) ? entry.State : null; state?.SetHealth(hp); if (hp is 0 or 100) lifeChanged = true; });
                interval.End();
                interval = phases?.Start(ArenaAllocationPhase.ReliableLifecycle) ?? default;
                foreach (var id in ended) _service!.PublishBullet(new BulletPose { Id = id }, true);
                if (Authority.Hits != previousHits) _service!.PublishScore(Authority.Shots, Authority.Hits);
                if (lifeChanged) _service!.PublishRoster(Authority.Snapshot(Scope));
                interval.End();
                interval = phases?.Start(ArenaAllocationPhase.MotionSend) ?? default;
                SendMotion();
                interval.End();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { FailHost(ex.Message); }
    }
    private async Task RunProbes(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_clientWire?.IsConnected != true) break;
                try { await _service!.QueryHostTick().WaitAsync(TimeSpan.FromSeconds(2), token); Interlocked.Increment(ref _probes); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception) { /* A probe is diagnostic, not the connection's lifecycle. */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void SendMotion()
    {
        if (_datagrams?.UnreliableEnabled != true) return;
        // Runtime fanout is atomic with respect to recipient readiness: skip this disposable
        // frame during admission/rebind rather than treating an unbound peer as a room failure.
        if (_runtime!.Members.Any(m => m.Peer.Value != LocalPeerId && m.Ready && !_datagrams.IsUnreliableReady(m.Peer))) return;
        var (players, bullets, tick) = Authority!.FillMotion(_playerBuffer, _bulletBuffer);
        SendBatches(tick, _playerBuffer, players, PlayerMotionMethod, (t, segment) => _service!.PublishPlayerMotion(t, segment));
        SendBatches(tick, _bulletBuffer, bullets, BulletMotionMethod, (t, segment) => _service!.PublishBulletMotion(t, segment));
    }
    private void SendBatches<T>(long tick, T[] buffer, int count, MethodInfo method, Action<long, ArraySegment<T>> publish)
    {
        int offset = 0;
        while (offset < count)
        {
            int size = 0;
            _measureArgs[0] = tick;
            while (offset + size < count)
            {
                _measureArgs[1] = new ArraySegment<T>(buffer, offset, size + 1);
                if (_runtime!.MeasureUnreliableCall(ServiceKey, method, _measureArgs) > _datagrams!.MaxUnreliablePayloadBytes) break;
                size++;
            }
            if (size == 0) throw new InvalidOperationException("A single motion record exceeds UDP payload limit");
            long before = GC.GetAllocatedBytesForCurrentThread();
            publish(tick, new ArraySegment<T>(buffer, offset, size));
            Interlocked.Add(ref _sendBytes, GC.GetAllocatedBytesForCurrentThread() - before);
            offset += size;
        }
    }
    private async Task RunLease(CancellationToken token)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3)); while (await timer.WaitForNextTickAsync(token)) if (!await _lobby.HeartbeatRoomAsync(RoomId, _hostToken)) { FailHost("Room lease expired"); break; } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { FailHost(ex.Message); }
    }
    private void FailHost(string reason)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        LastError = reason;
        _stop.Cancel();
        _routeLost.Cancel();
        // Do not await ourselves; disposal closes the room and all sockets.
        _ = Task.Run(async () => { try { await DisposeAsync(); } catch (Exception ex) { LastError = ex.Message; } });
    }
    public void ReceiveRoster(RoomSnapshot snapshot)
    {
        if (snapshot.Scope != Scope || snapshot.Players == null || snapshot.Players.Length > 64 || snapshot.Bullets == null || snapshot.Bullets.Length > 256) return;
        lock (_lifecycle)
        {
            lock (_gate) if (snapshot.Tick < _lastRosterTick && _view.Scope == Scope) return;
            var members = _runtime?.Members.Where(m => m.Ready).Select(m => m.Peer.Value).ToHashSet();
            var active = snapshot.Players.Where(p => p != null && p.MotionId > 0 && members?.Contains(p.PeerId) == true && !_retired.Contains(p.PeerId)).ToArray();
            foreach (var p in active) BindHealth(p.PeerId);
            lock (_gate)
            {
                if (_view.Scope != Scope)
                {
                    _view.Bullets = snapshot.Bullets.Select(ArenaAuthority.Copy).ToArray();
                    foreach (var b in _view.Bullets) _bulletTicks.TryAdd(b.Id, snapshot.Tick);
                }
                foreach (var p in active)
                {
                    var existing = _view.Players.FirstOrDefault(old => old.MotionId == p.MotionId);
                    if (existing != null) { p.X = existing.X; p.Z = existing.Z; p.AimX = existing.AimX; p.AimZ = existing.AimZ; }
                }
                _view.Players = active.Select(Clone).ToArray();
                _lastRosterTick = snapshot.Tick;
                _view.Scope = Scope;
                _view.TotalShots = snapshot.TotalShots;
                _view.TotalHits = snapshot.TotalHits;
                PruneMotionTicks();
            }
        }
    }
    public void ReceiveScore(int shots, int hits)
    { lock (_gate) { _view.TotalShots = Math.Max(_view.TotalShots, shots); _view.TotalHits = Math.Max(_view.TotalHits, hits); } }
    private void PruneMotionTicks()
    {
        var playerIds = _view.Players.Select(p => p.MotionId).ToHashSet();
        foreach (int id in _playerTicks.Keys.Where(id => !playerIds.Contains(id)).ToArray()) _playerTicks.Remove(id);
        var bulletIds = _view.Bullets.Select(b => b.Id).ToHashSet();
        foreach (long id in _bulletTicks.Keys.Where(id => !bulletIds.Contains(id)).ToArray()) _bulletTicks.Remove(id);
    }
    public void ReceivePlayerMotion(long tick, ArraySegment<PlayerMotionUpdate> updates)
    {
        if (updates.Count > 64 || updates.Array == null) return;
        lock (_gate)
        {
            Interlocked.Increment(ref _playerBatches);
            foreach (var update in updates)
            {
                if (!float.IsFinite(update.X) || !float.IsFinite(update.Z) || !float.IsFinite(update.AimX) || !float.IsFinite(update.AimZ)) continue;
                var player = _view.Players.FirstOrDefault(p => p.MotionId == update.Id);
                if (player == null || _playerTicks.GetValueOrDefault(update.Id, -1) >= tick) continue;
                player.X = update.X; player.Z = update.Z; player.AimX = update.AimX; player.AimZ = update.AimZ;
                _playerTicks[update.Id] = tick;
                _view.Tick = Math.Max(_view.Tick, tick);
                _positionTick = Math.Max(_positionTick, tick);
            }
        }
    }
    public void ReceiveBulletMotion(long tick, ArraySegment<BulletMotionUpdate> updates)
    {
        if (updates.Count > 256 || updates.Array == null) return;
        lock (_gate)
        {
            Interlocked.Increment(ref _bulletBatches);
            foreach (var update in updates)
            {
                if (!float.IsFinite(update.X) || !float.IsFinite(update.Z)) continue;
                var bullet = _view.Bullets.FirstOrDefault(b => b.Id == update.Id);
                if (bullet == null || _bulletTicks.GetValueOrDefault(update.Id, -1) >= tick) continue;
                bullet.X = update.X; bullet.Z = update.Z;
                _bulletTicks[update.Id] = tick;
                _view.Tick = Math.Max(_view.Tick, tick);
                _positionTick = Math.Max(_positionTick, tick);
            }
        }
    }
    public void ReceiveBullet(BulletPose bullet, bool ended)
    {
        lock (_gate)
        {
            var list = _view.Bullets.Where(b => b.Id != bullet.Id).Select(ArenaAuthority.Copy).ToList();
            if (!ended && list.Count < 256) list.Add(ArenaAuthority.Copy(bullet));
            _view.Bullets = list.ToArray();
            _bulletTicks.Remove(bullet.Id);
        }
    }
    private void ApplySnapshot(RoomSnapshot snapshot)
    {
        if (snapshot.Scope != Scope || snapshot.Players.Length > 64 || snapshot.Bullets.Length > 256) throw new InvalidOperationException("Invalid snapshot");
        lock (_lifecycle)
        {
            lock (_gate) if (_view.Scope == Scope && snapshot.Tick < _lastRosterTick) return;
            var members = _runtime?.Members.Where(m => m.Ready).Select(m => m.Peer.Value).ToHashSet();
            var active = snapshot.Players.Where(p => p != null && members?.Contains(p.PeerId) == true && !_retired.Contains(p.PeerId)).ToArray();
            foreach (var p in active) BindHealth(p.PeerId);
            lock (_gate)
            {
                foreach (var p in active)
                {
                    var existing = _view.Players.FirstOrDefault(old => old.MotionId == p.MotionId);
                    if (existing != null && _playerTicks.GetValueOrDefault(p.MotionId, -1) > snapshot.Tick)
                    { p.X = existing.X; p.Z = existing.Z; p.AimX = existing.AimX; p.AimZ = existing.AimZ; }
                }
                _view = new RoomSnapshot { Scope = Scope, Tick = Math.Max(snapshot.Tick, _view.Tick), Players = active.Select(Clone).ToArray(), Bullets = snapshot.Bullets.Select(ArenaAuthority.Copy).ToArray(), TotalShots = Math.Max(snapshot.TotalShots, _view.TotalShots), TotalHits = Math.Max(snapshot.TotalHits, _view.TotalHits) };
                if (_positionTick == 0) _positionTick = snapshot.Tick; // initial reliable discovery seeds position clock once
                foreach (var p in active) _playerTicks.TryAdd(p.MotionId, snapshot.Tick);
                foreach (var b in snapshot.Bullets) _bulletTicks.TryAdd(b.Id, snapshot.Tick);
                PruneMotionTicks();
            }
        }
    }
    private static PlayerPose Clone(PlayerPose p) => new() { MotionId = p.MotionId, PlayerId = p.PlayerId, PeerId = p.PeerId, Name = p.Name, X = p.X, Z = p.Z, AimX = p.AimX, AimZ = p.AimZ, Health = p.Health, Deaths = p.Deaths };
    public RoomSnapshot GetView()
    {
        if (_host && Authority != null) { var snap = Authority.Snapshot(Scope); lock (_gate) foreach (var p in snap.Players) if (_health.TryGetValue(p.PeerId, out var entry)) p.Health = entry.State.Health; return snap; }
        lock (_gate) return new RoomSnapshot { Scope = _view.Scope, Tick = _view.Tick, Players = _view.Players.Select(p => { var clone = Clone(p); if (_health.TryGetValue(p.PeerId, out var state)) clone.Health = state.State.Health; return clone; }).ToArray(), Bullets = _view.Bullets.Select(ArenaAuthority.Copy).ToArray(), TotalShots = _view.TotalShots, TotalHits = _view.TotalHits };
    }
    public void SubmitMove(float x, float z, uint sequence) { if (IsReady) _service!.SubmitMove(x, z, sequence); }
    public void Fire(float directionX, float directionZ, uint sequence) { if (IsReady) _service!.SubmitShot(directionX, directionZ, sequence); }
    public ArenaDatagramReport GetDatagramReport()
    {
        var stats = _datagrams?.GetDatagramStatistics() ?? new DatagramStatistics();
        return new ArenaDatagramReport { Enabled = _datagrams?.UnreliableEnabled == true,
            Ready = _datagrams != null && (_host ? _runtime?.Members.Where(m => m.Peer.Value != LocalPeerId && m.Ready).All(m => _datagrams.IsUnreliableReady(m.Peer)) == true : _runtime != null && _datagrams.IsUnreliableReady(_runtime.HostPeerId)),
            PayloadLimit = _datagrams?.MaxUnreliablePayloadBytes ?? 0,
            SentDatagrams = stats.SentDatagrams, ReceivedDatagrams = stats.ReceivedDatagrams,
            RejectedDatagrams = stats.RejectedDatagrams, DroppedDatagrams = stats.DroppedDatagrams,
            LargestDatagramBytes = stats.LargestDatagramBytes, PositionTick = Interlocked.Read(ref _positionTick),
            PlayerBatchesReceived = Interlocked.Read(ref _playerBatches), BulletBatchesReceived = Interlocked.Read(ref _bulletBatches),
            ReliableProbesCompleted = Interlocked.Read(ref _probes), HealthUpdatesReceived = Interlocked.Read(ref _healthUpdates),
            PoseBufferAllocations = _host ? 2 : 0, PositionSendAllocatedBytes = Interlocked.Read(ref _sendBytes) };
    }
    public void SetUdpEnabled(bool enabled) { if (_datagrams == null) throw new InvalidOperationException("UDP lane unavailable"); _datagrams.UnreliableEnabled = enabled; }
    public Task RebindUdpAsync(CancellationToken cancellationToken = default) => _host
        ? throw new InvalidOperationException("Only clients may rebind their UDP endpoint")
        : _datagrams?.RebindDatagramsAsync(cancellationToken) ?? throw new InvalidOperationException("UDP lane unavailable");
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _routeLost.Cancel();
        if (_tickTask != null) await _tickTask;
        if (_leaseTask != null) await _leaseTask;
        if (_probeTask != null) await _probeTask;
        if (_clientWire?.IsConnected == true && _notifyLeaving != null && !string.IsNullOrEmpty(LocalPeerId))
            try { await _notifyLeaving(); }
            catch (Exception ex) { LastError = "Leave acknowledgement failed (" + ex.GetType().Name + ")"; /* physical TCP closure is the fallback */ }
        _runtime?.Dispose(); _clientWire?.Dispose(); _hostWire?.Dispose();
        if (_host && !string.IsNullOrEmpty(_hostToken)) try { await _lobby.CloseRoomAsync(RoomId, _hostToken); } catch (Exception ex) { LastError = ex.Message; }
        _stop.Dispose();
        _routeLost.Dispose();
    }
    private static string Endpoint(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
}
