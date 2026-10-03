using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using Xunit;

namespace BITKit.Multiplayer.Samples.Arena;

public sealed class ArenaRelayTests
{
    [Fact] public async Task RelayHostAndClientsUsePublishedRoomWithoutGameListener()
    {
        var lobby = new RelayLobby();
        using var portOwner = new TcpListener(IPAddress.Loopback, 0);
        portOwner.Start();
        int gamePort = ((IPEndPoint)portOwner.LocalEndpoint).Port;
        using var relay = new TouchSocketRelayServer(new LobbyAuthorizer(lobby));
        int relayPort = GameTestPorts.FreeTcpUdpPort();
        await relay.StartAsync(new RelayListenOptions { Port = relayPort, BindAddress = IPAddress.Loopback, VerifyToken = ArenaProtocol.RelayToken });
        var hostAuth = await lobby.GuestAsync("Host");
        var firstAuth = await lobby.GuestAsync("First");
        var lateAuth = await lobby.GuestAsync("Late");
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth,
            new ArenaHostOptions { Port = gamePort, ConnectionMode = ArenaConnectionMode.Relay, RelayPort = relayPort });
        Assert.Equal(ArenaConnectionMode.Relay, host.ConnectionMode);
        Assert.Equal($"127.0.0.1:{relayPort}", host.ConnectedEndpoint);
        Assert.True(host.IsReady);
        await using var first = await ArenaSession.StartClientAsync(lobby, firstAuth, new ArenaClientOptions { RoomId = host.RoomId });
        Assert.Equal(ArenaConnectionMode.Relay, first.ConnectionMode);
        Assert.Equal(host.ConnectedEndpoint, first.ConnectedEndpoint);
        await WaitUntil(() => host.GetView().Players.Length == 2 && first.GetView().Players.Length == 2);
        // The provisional-peer adapter must select the borrowed-memory receive path,
        // including both reliable v4 notification and Task request; no legacy duplicate.
        var adapter = typeof(ArenaSessionImpl).GetField("_hostWire", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
        var memory = Assert.IsAssignableFrom<IRoomMemoryWire>(adapter);
        var legacy = Assert.IsAssignableFrom<IRoomWire>(adapter);
        int notifications = 0, requests = 0, duplicated = 0;
        memory.MemoryReceived += (_, payload) =>
        {
            var bytes = payload.Span; // Borrowed: inspect only within this callback.
            if (bytes.Length < 3 || bytes[0] != 0xB6 || bytes[1] != 4) return;
            if (bytes[2] == 1) Interlocked.Increment(ref notifications);
            if (bytes[2] == 2) Interlocked.Increment(ref requests);
        };
        legacy.Received += (_, _) => Interlocked.Increment(ref duplicated);
        first.SubmitMove(1, 0, 1);
        var service = (ArenaGameService)typeof(ArenaSessionImpl).GetField("_service", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first)!;
        var requested = await service.RequestSnapshot().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, requested.Players.Length);
        await WaitUntil(() => Volatile.Read(ref notifications) > 0 && Volatile.Read(ref requests) > 0 &&
            first.GetDatagramReport().PlayerBatchesReceived > 0);
        Assert.Equal(0, Volatile.Read(ref duplicated));
        Assert.Equal("native-udp", host.TransportName);
        Assert.Equal("native-udp", first.TransportName);
        host.Fire(1, 0, 1);
        await WaitUntil(() => first.GetView().Players.Single(p => p.PlayerId == first.LocalPlayerId).Health == 75);
        await using (var late = await ArenaSession.StartClientAsync(lobby, lateAuth, new ArenaClientOptions { RoomId = host.RoomId }))
        {
            await WaitUntil(() => late.GetView().Players.Length == 3 &&
                late.GetView().Players.Single(p => p.PlayerId == first.LocalPlayerId).Health == 75);
            Assert.True(late.IsReady);
        }
        await WaitUntil(() => host.GetView().Players.Length == 2);
        Assert.True(relay.GetStatistics().ForwardedMessages > 0);
        relay.Dispose();
        await WaitUntil(() => !host.IsReady && !first.IsReady && lobby.RoomClosed);
        await WaitUntil(() => host.LastError == "Game route disconnected");
    }

    [Fact] public async Task RelayUdpPauseKeepsReliableHealthAndQueryAliveThenRecovers()
    {
        var lobby = new RelayLobby();
        int relayPort = GameTestPorts.FreeTcpUdpPort();
        using var relay = new TouchSocketRelayServer(new LobbyAuthorizer(lobby));
        await relay.StartAsync(new RelayListenOptions { Port = relayPort, BindAddress = IPAddress.Loopback, VerifyToken = ArenaProtocol.RelayToken });
        var hostAuth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest"); var lateAuth = await lobby.GuestAsync("Late");
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth,
            new ArenaHostOptions { ConnectionMode = ArenaConnectionMode.Relay, RelayPort = relayPort });
        await using var client = await ArenaSession.StartClientAsync(lobby, guest, new ArenaClientOptions { RoomId = host.RoomId });
        await WaitUntil(() => client.GetDatagramReport().PlayerBatchesReceived > 2);
        relay.UdpForwardingEnabled = false;
        await Task.Delay(350); // drain packets already in flight before observing the stopped lane
        long tick = client.GetDatagramReport().PositionTick;
        long probes = client.GetDatagramReport().ReliableProbesCompleted;
        host.Fire(1, 0, 1);
        await WaitUntil(() => client.GetView().Players.Single(p => p.PlayerId == client.LocalPlayerId).Health == 75 &&
            client.GetDatagramReport().ReliableProbesCompleted > probes);
        Assert.Equal(tick, client.GetDatagramReport().PositionTick);
        Assert.True(client.GetDatagramReport().HealthUpdatesReceived > 0);
        Assert.True(host.IsReady); Assert.True(client.IsReady);
        await using (var late = await ArenaSession.StartClientAsync(lobby, lateAuth, new ArenaClientOptions { RoomId = host.RoomId }))
        {
            await WaitUntil(() => late.GetView().Players.Length == 3);
            Assert.Equal(75, late.GetView().Players.Single(p => p.PlayerId == guest.PlayerId).Health);
            Assert.Equal(3, late.GetView().Players.Select(p => p.MotionId).Distinct().Count());
        }
        relay.UdpForwardingEnabled = true;
        await WaitUntil(() => client.GetDatagramReport().PositionTick > tick);
        Assert.True(relay.GetStatistics().UdpForwardedDatagrams > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedRelayAdmissionDoesNotLeaveGhostAndNextClientCanJoin(bool waitForTimeout)
    {
        var lobby = new RelayLobby();
        int relayPort = GameTestPorts.FreeTcpUdpPort();
        using var relay = new TouchSocketRelayServer(new LobbyAuthorizer(lobby));
        await relay.StartAsync(new RelayListenOptions { Port = relayPort, BindAddress = IPAddress.Loopback, VerifyToken = ArenaProtocol.RelayToken });
        var hostAuth = await lobby.GuestAsync("Host");
        var interruptedAuth = await lobby.GuestAsync("Interrupted");
        var nextAuth = await lobby.GuestAsync("Next");
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth,
            new ArenaHostOptions { ConnectionMode = ArenaConnectionMode.Relay, RelayPort = relayPort });
        var ticket = await lobby.JoinRoomAsync(interruptedAuth.SessionToken, host.RoomId);
        lobby.RedemptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lobby.ReleaseRedemption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var interrupted = new RelayClientWire(new BITKit.Multiplayer.PeerId(host.LocalPeerId));
        await interrupted.ConnectAsync(new RelayConnectOptions { Address = ticket.Room.Address, Port = ticket.Room.Port,
            VerifyToken = ArenaProtocol.RelayToken, RoomId = ticket.Room.RoomId, Scope = ticket.Room.Scope,
            HostPeerId = new BITKit.Multiplayer.PeerId(ticket.Room.HostPeerId) });
        var admission = interrupted.RequestAdmissionAsync(ticket.Ticket);
        await lobby.RedemptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (waitForTimeout) await Task.Delay(TimeSpan.FromSeconds(6));
        interrupted.Dispose();
        lobby.ReleaseRedemption.SetResult();
        try { await admission.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException) { throw; }
        catch (Exception) { /* denied or disconnected */ }
        await lobby.RedemptionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300); // Allow the relay's late result/abort round trip to finish.
        await WaitUntil(() => host.GetView().Players.Length == 1);
        Assert.Equal(host.LocalPeerId, Assert.Single(host.GetView().Players).PeerId);
        Assert.True(host.IsReady);
        Assert.Equal("", host.LastError);
        await using var next = await ArenaSession.StartClientAsync(lobby, nextAuth, new ArenaClientOptions { RoomId = host.RoomId });
        await WaitUntil(() => host.GetView().Players.Length == 2 && next.GetView().Players.Length == 2);
        await Task.Delay(300); // Late abort callbacks must not erase the healthy replacement.
        Assert.Equal("", host.LastError);
        Assert.True(next.IsReady);
        Assert.Contains(host.GetView().Players, p => p.PeerId == next.LocalPeerId);
    }

    [Fact] public async Task RejectedDuplicatePeerNeverRetiresTheExistingClient()
    {
        var lobby = new RelayLobby();
        int relayPort = GameTestPorts.FreeTcpUdpPort();
        using var relay = new TouchSocketRelayServer(new LobbyAuthorizer(lobby));
        await relay.StartAsync(new RelayListenOptions { Port = relayPort, BindAddress = IPAddress.Loopback, VerifyToken = ArenaProtocol.RelayToken });
        var hostAuth = await lobby.GuestAsync("Host");
        var firstAuth = await lobby.GuestAsync("First");
        var duplicateAuth = await lobby.GuestAsync("Duplicate");
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth,
            new ArenaHostOptions { ConnectionMode = ArenaConnectionMode.Relay, RelayPort = relayPort });
        await using var first = await ArenaSession.StartClientAsync(lobby, firstAuth, new ArenaClientOptions { RoomId = host.RoomId });
        await WaitUntil(() => host.GetView().Players.Length == 2);
        lobby.NextPeerId = first.LocalPeerId;
        await Assert.ThrowsAnyAsync<Exception>(() => ArenaSession.StartClientAsync(lobby, duplicateAuth, new ArenaClientOptions { RoomId = host.RoomId }));
        await Task.Delay(300);
        Assert.True(first.IsReady);
        Assert.Equal(2, host.GetView().Players.Length);
        Assert.Equal(firstAuth.PlayerId, host.GetView().Players.Single(p => p.PeerId == first.LocalPeerId).PlayerId);
        Assert.Equal("", host.LastError);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(25, timeout.Token); }
    }

    private sealed class LobbyAuthorizer : IRelayRoomAuthorizer
    {
        private readonly RelayLobby _lobby;
        public LobbyAuthorizer(RelayLobby lobby) => _lobby = lobby;
        public async Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token)
        {
            var room = await _lobby.ValidateRoomHostAsync(roomId, credential);
            return room == null ? null : new RelayRoomIdentity { Scope = room.Scope, HostPeerId = new BITKit.Multiplayer.PeerId(room.HostPeerId) };
        }
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token)
            => _lobby.PublishRelayRoomAsync(roomId, credential);
        public async Task CloseRoomAsync(string roomId, string credential, CancellationToken token)
            => await _lobby.CloseRoomAsync(roomId, credential);
    }

    private sealed class RelayLobby : IArenaRelayLobbyApi
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AuthSession> _sessions = new();
        private readonly Dictionary<string, AdmissionIdentity> _tickets = new();
        private RoomInfo? _room;
        private bool _published;
        public string? NextPeerId { get; set; }
        public TaskCompletionSource? RedemptionStarted { get; set; }
        public TaskCompletionSource? ReleaseRedemption { get; set; }
        public TaskCompletionSource RedemptionCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RoomClosed { get { lock (_gate) return _room == null; } }
        public Task<AuthSession> GuestAsync(string name)
        {
            var auth = new AuthSession { Success = true, PlayerId = Guid.NewGuid().ToString("N"), DisplayName = name, SessionToken = Guid.NewGuid().ToString("N") };
            lock (_gate) _sessions.Add(auth.SessionToken, auth);
            return Task.FromResult(auth);
        }
        public Task<AuthSession> RegisterAsync(string username, string password) => GuestAsync(username);
        public Task<AuthSession> LoginAsync(string username, string password) => GuestAsync(username);
        public Task<RoomRegistration> ReserveRelayRoomAsync(string token, string name, string address, int port, bool tls)
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(token)) return Task.FromResult(new RoomRegistration { Error = "Denied" });
                _room = new RoomInfo { RoomId = Guid.NewGuid().ToString("N"), Scope = Guid.NewGuid().ToString("N"), HostPeerId = Guid.NewGuid().ToString("N"), Name = name, Address = address, Port = port, ConnectionMode = ArenaConnectionMode.Relay, UseTls = tls };
                _published = false;
                return Task.FromResult(new RoomRegistration { Success = true, Room = _room, HostToken = "host-secret" });
            }
        }
        public Task<RoomInfo?> ValidateRoomHostAsync(string roomId, string token)
        { lock (_gate) return Task.FromResult(_room?.RoomId == roomId && token == "host-secret" ? _room : null); }
        public Task<bool> PublishRelayRoomAsync(string roomId, string token)
        { lock (_gate) { bool valid = _room?.RoomId == roomId && token == "host-secret"; if (valid) _published = true; return Task.FromResult(valid); } }
        public Task<RoomRegistration> CreateRoomAsync(string token, string name, string address, int port) => throw new InvalidOperationException("Relay must not create a direct room");
        public Task<RoomInfo[]> ListRoomsAsync(string token)
        { lock (_gate) return Task.FromResult(_published && _room != null && _sessions.ContainsKey(token) ? new[] { _room } : Array.Empty<RoomInfo>()); }
        public Task<JoinTicket> JoinRoomAsync(string token, string roomId)
        {
            lock (_gate)
            {
                if (!_published || _room?.RoomId != roomId || !_sessions.TryGetValue(token, out var auth)) return Task.FromResult(new JoinTicket { Error = "Denied" });
                string ticket = Guid.NewGuid().ToString("N");
                string peerId = NextPeerId ?? Guid.NewGuid().ToString("N");
                NextPeerId = null;
                _tickets.Add(ticket, new AdmissionIdentity { Success = true, PlayerId = auth.PlayerId, DisplayName = auth.DisplayName, PeerId = peerId });
                return Task.FromResult(new JoinTicket { Success = true, Room = _room, Ticket = ticket });
            }
        }
        public async Task<AdmissionIdentity> RedeemTicketAsync(string roomId, string token, string ticket)
        {
            var started = RedemptionStarted;
            var release = ReleaseRedemption;
            if (release != null) { started?.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
            lock (_gate)
            {
                var result = _room?.RoomId == roomId && token == "host-secret" && _tickets.Remove(ticket, out var identity)
                    ? identity : new AdmissionIdentity { Error = "Denied" };
                RedemptionCompleted.TrySetResult();
                return result;
            }
        }
        public Task<bool> HeartbeatRoomAsync(string roomId, string token)
        { lock (_gate) return Task.FromResult(_room?.RoomId == roomId && token == "host-secret"); }
        public Task<bool> CloseRoomAsync(string roomId, string token)
        { lock (_gate) { bool valid = _room?.RoomId == roomId && token == "host-secret"; if (valid) { _room = null; _published = false; } return Task.FromResult(valid); } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
