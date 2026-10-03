using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BITKit.Multiplayer.Samples.Arena;
using BITKit.Multiplayer.TouchSocket;
using Xunit;

public sealed class LobbyTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("wrong.example.invalid")]
    public async Task LobbyTlsRejectsUntrustedAndWrongHostWithoutChangingMachineTrust(string targetHost)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), "arena-lobby-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        var password = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, password));
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await using var server = new ArenaLobbyServer(new ArenaLobbyStore());
            await server.StartAsync(port, IPAddress.Loopback, path, password);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Matching hostname must still fail because this certificate is not trusted.
            // The mismatched hostname additionally fails the TLS target-host check.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var client = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port, true, targetHost, deadline.Token);
                await client.GuestAsync("TLS verification must reject this certificate");
            });
            Assert.False(deadline.IsCancellationRequested, "TLS rejection must not be caused by the deadline");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ClockExpiryAndWrongHostNeverConsumeTicket()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var store = new ArenaLobbyStore(clock: () => now);
        var host = await store.GuestAsync("Host");
        var guest = await store.GuestAsync("Guest");
        var room = await store.CreateRoomAsync(host.SessionToken, "Arena", "127.0.0.1", 12345);
        var other = await store.CreateRoomAsync(host.SessionToken, "Other", "127.0.0.1", 12346);
        var join = await store.JoinRoomAsync(guest.SessionToken, room.Room.RoomId);
        Assert.True(join.Success);
        Assert.False((await store.RedeemTicketAsync(other.Room.RoomId, other.HostToken, join.Ticket)).Success);
        Assert.False((await store.RedeemTicketAsync(room.Room.RoomId, other.HostToken, join.Ticket)).Success);
        var admitted = await store.RedeemTicketAsync(room.Room.RoomId, room.HostToken, join.Ticket);
        Assert.True(admitted.Success);
        Assert.Equal(guest.PlayerId, admitted.PlayerId);
        Assert.NotEmpty(admitted.PeerId);
        Assert.False((await store.RedeemTicketAsync(room.Room.RoomId, room.HostToken, join.Ticket)).Success);
        var second = await store.JoinRoomAsync(guest.SessionToken, room.Room.RoomId);
        var secondAdmission = await store.RedeemTicketAsync(room.Room.RoomId, room.HostToken, second.Ticket);
        Assert.Equal(admitted.PlayerId, secondAdmission.PlayerId);
        Assert.NotEqual(admitted.PeerId, secondAdmission.PeerId);
        var expires = await store.JoinRoomAsync(guest.SessionToken, room.Room.RoomId);
        now += ArenaLobbyStore.TicketLifetime;
        Assert.False((await store.RedeemTicketAsync(room.Room.RoomId, room.HostToken, expires.Ticket)).Success);
        Assert.True(await store.HeartbeatRoomAsync(room.Room.RoomId, room.HostToken));
        now += ArenaLobbyStore.RoomLifetime;
        Assert.False(await store.HeartbeatRoomAsync(room.Room.RoomId, room.HostToken));
        Assert.Empty(await store.ListRoomsAsync(guest.SessionToken));
        now += ArenaLobbyStore.SessionLifetime;
        Assert.False((await store.CreateRoomAsync(host.SessionToken, "X", "127.0.0.1", 12345)).Success);
    }

    [Fact]
    public async Task PersistOnlyAccountsNotEphemeralCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), "arena-lobby-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ArenaLobbyStore(path);
            var session = await store.RegisterAsync("pilot_01", "SecretPass123!");
            Assert.True(session.Success);
            var contents = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SecretPass123!", contents);
            Assert.DoesNotContain(session.SessionToken, contents);
            var restarted = new ArenaLobbyStore(path);
            Assert.False((await restarted.CreateRoomAsync(session.SessionToken, "X", "127.0.0.1", 12345)).Success);
            Assert.False((await restarted.LoginAsync("pilot_01", "wrongpassword")).Success);
            var login = await restarted.LoginAsync("pilot_01", "SecretPass123!");
            Assert.True(login.Success);
            Assert.Equal(session.PlayerId, login.PlayerId);
            Assert.NotEqual(session.SessionToken, login.SessionToken);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RealDmtpInterfaceLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var server = new ArenaLobbyServer(new ArenaLobbyStore());
        await server.StartAsync(port);
        await using IArenaLobbyApi host = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port);
        await using IArenaLobbyApi player = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port);
        var hostSession = await host.RegisterAsync("hostpilot", "SecretPass123!");
        var member = await player.GuestAsync("Visitor");
        Assert.True(hostSession.Success);
        Assert.True(member.Success);
        Assert.False((await player.LoginAsync("hostpilot", "wrongpassword")).Success);
        var room = await host.CreateRoomAsync(hostSession.SessionToken, "Lobby loopback", "127.0.0.1", 17891);
        Assert.True(room.Success);
        Assert.Contains(await player.ListRoomsAsync(member.SessionToken), r => r.RoomId == room.Room.RoomId);
        var ticket = await player.JoinRoomAsync(member.SessionToken, room.Room.RoomId);
        Assert.True(ticket.Success);
        Assert.False((await host.RedeemTicketAsync(room.Room.RoomId, "invalid", ticket.Ticket)).Success);
        var admitted = await host.RedeemTicketAsync(room.Room.RoomId, room.HostToken, ticket.Ticket);
        Assert.True(admitted.Success);
        Assert.Equal(member.PlayerId, admitted.PlayerId);
        Assert.True(await host.HeartbeatRoomAsync(room.Room.RoomId, room.HostToken));
        Assert.True(await host.CloseRoomAsync(room.Room.RoomId, room.HostToken));
        Assert.Empty(await player.ListRoomsAsync(member.SessionToken));
    }

    [Fact]
    public async Task PendingRelayIsPrivateUntilAuthenticatedActivationAndExpiryCleansTickets()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var store = new ArenaLobbyStore(clock: () => now);
        var host = await store.GuestAsync("Host");
        var player = await store.GuestAsync("Player");
        var pending = await store.ReserveRelayRoomAsync(host.SessionToken, "Pending", "relay.example.org", 17892, true);
        Assert.True(pending.Success);
        Assert.Equal(ArenaConnectionMode.Relay, pending.Room.ConnectionMode);
        Assert.True(pending.Room.UseTls);
        Assert.Equal("relay.example.org", pending.Room.Address);
        Assert.Empty(await store.ListRoomsAsync(player.SessionToken));
        Assert.False((await store.JoinRoomAsync(player.SessionToken, pending.Room.RoomId)).Success);
        var abandoned = await store.ReserveRelayRoomAsync(host.SessionToken, "Abandoned", "relay.example.org", 17892, false);
        Assert.True(await store.CloseRoomAsync(abandoned.Room.RoomId, abandoned.HostToken));
        Assert.Null(await store.ValidateRoomHostAsync(abandoned.Room.RoomId, abandoned.HostToken));
        var expiredPending = await store.ReserveRelayRoomAsync(host.SessionToken, "Expired", "relay.example.org", 17892, false);
        Assert.Null(await store.ValidateRoomHostAsync(pending.Room.RoomId, "wrong"));
        var validated = await store.ValidateRoomHostAsync(pending.Room.RoomId, pending.HostToken);
        Assert.NotNull(validated);
        validated.Name = "tampered";
        Assert.Equal("Pending", (await store.ValidateRoomHostAsync(pending.Room.RoomId, pending.HostToken))!.Name);
        Assert.False(await store.PublishRelayRoomAsync(pending.Room.RoomId, "wrong"));
        Assert.True(await store.HeartbeatRoomAsync(pending.Room.RoomId, pending.HostToken));
        Assert.Empty(await store.ListRoomsAsync(player.SessionToken));
        Assert.True(await store.PublishRelayRoomAsync(pending.Room.RoomId, pending.HostToken));
        var listed = Assert.Single(await store.ListRoomsAsync(player.SessionToken));
        Assert.Equal(ArenaConnectionMode.Relay, listed.ConnectionMode);
        Assert.True(listed.UseTls);
        Assert.Equal("relay.example.org", listed.Address);
        var ticket = await store.JoinRoomAsync(player.SessionToken, pending.Room.RoomId);
        Assert.True(ticket.Success);
        Assert.False((await store.RedeemTicketAsync(pending.Room.RoomId, "wrong", ticket.Ticket)).Success);
        Assert.True((await store.RedeemTicketAsync(pending.Room.RoomId, pending.HostToken, ticket.Ticket)).Success);
        now += ArenaLobbyStore.RoomLifetime;
        Assert.Empty(await store.ListRoomsAsync(player.SessionToken));
        Assert.Null(await store.ValidateRoomHostAsync(expiredPending.Room.RoomId, expiredPending.HostToken));
        Assert.Null(await store.ValidateRoomHostAsync(pending.Room.RoomId, pending.HostToken));
        Assert.False(await store.PublishRelayRoomAsync(pending.Room.RoomId, pending.HostToken));
        Assert.False((await store.JoinRoomAsync(player.SessionToken, pending.Room.RoomId)).Success);
    }

    [Fact]
    public async Task RelayDmtpRoundTripUsesExactExtensionAndPreservesDirectRooms()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var server = new ArenaLobbyServer(new ArenaLobbyStore());
        await server.StartAsync(port);
        await using IArenaRelayLobbyApi host = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port);
        await using IArenaRelayLobbyApi player = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port);
        var session = await host.GuestAsync("Host");
        var guest = await player.GuestAsync("Guest");
        var direct = await host.CreateRoomAsync(session.SessionToken, "Direct", "127.0.0.1", 17891);
        Assert.False(await host.PublishRelayRoomAsync(direct.Room.RoomId, direct.HostToken));
        Assert.Equal(ArenaConnectionMode.Direct, Assert.Single(await player.ListRoomsAsync(guest.SessionToken)).ConnectionMode);
        var relay = await host.ReserveRelayRoomAsync(session.SessionToken, "Relay", "relay.example.org", 17892, true);
        Assert.DoesNotContain(await player.ListRoomsAsync(guest.SessionToken), r => r.RoomId == relay.Room.RoomId);
        Assert.False((await player.JoinRoomAsync(guest.SessionToken, relay.Room.RoomId)).Success);
        Assert.Null(await host.ValidateRoomHostAsync(relay.Room.RoomId, direct.HostToken));
        Assert.False(await host.PublishRelayRoomAsync(relay.Room.RoomId, direct.HostToken));
        Assert.Equal(relay.Room.Scope, (await host.ValidateRoomHostAsync(relay.Room.RoomId, relay.HostToken))!.Scope);
        Assert.True(await host.PublishRelayRoomAsync(relay.Room.RoomId, relay.HostToken));
        var listed = Assert.Single(await player.ListRoomsAsync(guest.SessionToken), r => r.RoomId == relay.Room.RoomId);
        Assert.Equal("relay.example.org", listed.Address);
        Assert.Equal(17892, listed.Port);
        Assert.Equal(ArenaConnectionMode.Relay, listed.ConnectionMode);
        Assert.True(listed.UseTls);
        var ticket = await player.JoinRoomAsync(guest.SessionToken, relay.Room.RoomId);
        Assert.True(ticket.Success);
        Assert.False((await host.RedeemTicketAsync(relay.Room.RoomId, direct.HostToken, ticket.Ticket)).Success);
        Assert.True((await host.RedeemTicketAsync(relay.Room.RoomId, relay.HostToken, ticket.Ticket)).Success);
        Assert.True(await host.CloseRoomAsync(relay.Room.RoomId, relay.HostToken));
        Assert.Null(await host.ValidateRoomHostAsync(relay.Room.RoomId, relay.HostToken));
        Assert.False((await player.JoinRoomAsync(guest.SessionToken, relay.Room.RoomId)).Success);
    }

    [Fact]
    public async Task RelayAuthorizerOnlyActivatesAuthenticatedReservedHostOverDmtp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var server = new ArenaLobbyServer(new ArenaLobbyStore());
        await server.StartAsync(port);
        await using IArenaRelayLobbyApi lobby = await ArenaLobbyClient.ConnectAsync("127.0.0.1", port);
        var session = await lobby.GuestAsync("Host");
        var room = await lobby.ReserveRelayRoomAsync(session.SessionToken, "Test", "127.0.0.1", 17892, false);
        var direct = await lobby.CreateRoomAsync(session.SessionToken, "Direct", "127.0.0.1", 17891);
        var authorizer = new ArenaRelayAuthorizer(lobby);
        Assert.False(await authorizer.ActivateRoomAsync(room.Room.RoomId, room.HostToken, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeHostAsync(room.Room.RoomId, direct.HostToken, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeHostAsync(direct.Room.RoomId, direct.HostToken, CancellationToken.None));
        Assert.False(await authorizer.ActivateRoomAsync(room.Room.RoomId, direct.HostToken, CancellationToken.None));
        var identity = await authorizer.AuthorizeHostAsync(room.Room.RoomId, room.HostToken, CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal(room.Room.Scope, identity.Scope);
        Assert.Equal(room.Room.HostPeerId, identity.HostPeerId.Value);
        Assert.True(await authorizer.ActivateRoomAsync(room.Room.RoomId, room.HostToken, CancellationToken.None));
        Assert.Contains(await lobby.ListRoomsAsync(session.SessionToken), r => r.RoomId == room.Room.RoomId);
        await authorizer.CloseRoomAsync(room.Room.RoomId, direct.HostToken, CancellationToken.None);
        Assert.NotNull(await lobby.ValidateRoomHostAsync(room.Room.RoomId, room.HostToken));
        await authorizer.CloseRoomAsync(room.Room.RoomId, room.HostToken, CancellationToken.None);
        Assert.Null(await lobby.ValidateRoomHostAsync(room.Room.RoomId, room.HostToken));
    }

    [Fact]
    public async Task RealRelayHostActivationPublishesThroughLobbyAndDisconnectClosesRoom()
    {
        static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var lobbyPort = FreePort();
        var relayPort = FreePort();
        await using var server = new ArenaLobbyServer(new ArenaLobbyStore());
        await server.StartAsync(lobbyPort);
        await using IArenaRelayLobbyApi lobby = await ArenaLobbyClient.ConnectAsync("127.0.0.1", lobbyPort);
        var session = await lobby.GuestAsync("Host");
        var pending = await lobby.ReserveRelayRoomAsync(session.SessionToken, "Real Relay", "127.0.0.1", relayPort, false);
        using var relay = new TouchSocketRelayServer(new ArenaRelayAuthorizer(lobby));
        var relayErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        relay.Diagnostic += relayErrors.Enqueue;
        await relay.StartAsync(new RelayListenOptions { Port = relayPort, BindAddress = IPAddress.Loopback });
        using var host = new RelayHostWire();
        try { await host.ConnectAsync(new RelayConnectOptions
        {
            Address = "127.0.0.1", Port = relayPort, RoomId = pending.Room.RoomId,
            Scope = pending.Room.Scope, HostPeerId = new BITKit.Multiplayer.PeerId(pending.Room.HostPeerId),
            HostCredential = pending.HostToken
        }, _ => throw new InvalidOperationException("No admission requested"), _ => { }); }
        catch (Exception ex) { throw new InvalidOperationException("Host registration failed (relay diagnostics: " + relayErrors.Count + ")", ex); }
        Assert.DoesNotContain(await lobby.ListRoomsAsync(session.SessionToken), r => r.RoomId == pending.Room.RoomId);
        Assert.Equal(1, relay.GetStatistics().Rooms);
        await host.ActivateAsync();
        Assert.Contains(await lobby.ListRoomsAsync(session.SessionToken), r => r.RoomId == pending.Room.RoomId);
        host.Dispose();
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (await lobby.ValidateRoomHostAsync(pending.Room.RoomId, pending.HostToken) is not null && DateTime.UtcNow < timeout)
            await Task.Delay(25);
        Assert.Null(await lobby.ValidateRoomHostAsync(pending.Room.RoomId, pending.HostToken));
    }
}
