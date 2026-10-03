using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Sockets;
using Xunit;

namespace RelayTests;

public sealed class RelaySocketTests
{
    private sealed class Authorizer : IRelayRoomAuthorizer
    {
        public readonly ConcurrentDictionary<string, string> Active = new();
        public readonly ConcurrentDictionary<string, int> Closed = new();
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token) =>
            Task.FromResult<RelayRoomIdentity?>(credential == "secret-" + roomId ? new RelayRoomIdentity { Scope = "scope-" + roomId, HostPeerId = new PeerId("host-" + roomId) } : null);
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token)
        { Active[roomId] = credential; return Task.FromResult(credential == "secret-" + roomId); }
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token)
        { Closed.AddOrUpdate(roomId, 1, (_, n) => n + 1); return Task.CompletedTask; }
    }
    private sealed class DelayedActivation : IRelayRoomAuthorizer
    {
        public readonly TaskCompletionSource<bool> Started = Signal<bool>();
        public readonly TaskCompletionSource<bool> Publish = Signal<bool>();
        public int Closes;
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token)
            => Task.FromResult<RelayRoomIdentity?>(new RelayRoomIdentity { Scope = "scope-one", HostPeerId = new PeerId("host-one") });
        public async Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token)
        { Started.TrySetResult(true); return await Publish.Task; }
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token)
        { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }
    private sealed class DelayedClose : IRelayRoomAuthorizer
    {
        public readonly TaskCompletionSource<bool> CloseEntered = Signal<bool>();
        public readonly TaskCompletionSource<bool> FinishClose = Signal<bool>();
        public int Authorizations;
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token)
        {
            Interlocked.Increment(ref Authorizations);
            return Task.FromResult<RelayRoomIdentity?>(credential == "secret-one" ?
                new RelayRoomIdentity { Scope = "scope-one", HostPeerId = new PeerId("host-one") } : null);
        }
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token) => Task.FromResult(true);
        public async Task CloseRoomAsync(string roomId, string credential, CancellationToken token)
        { CloseEntered.TrySetResult(true); await FinishClose.Task; }
    }
    private static int FreePort()
    {
        // A TCP-free ephemeral port may already belong to another UDP endpoint.
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
            finally { listener.Stop(); }
        }
        throw new InvalidOperationException("Could not reserve a port available to both TCP and UDP.");
    }
    private static RelayConnectOptions Options(int port, string room) => new() { Port = port, RoomId = room, Scope = "scope-" + room, HostPeerId = new PeerId("host-" + room), HostCredential = "secret-" + room };
    private static async Task Eventually(Func<bool> predicate)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(predicate());
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] DmtpFrame(byte op, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(op); body(writer); return stream.ToArray();
    }

    [Fact]
    public async Task RoomsAuthenticateRouteAndRetireOverRealSockets()
    {
        int port = FreePort(); var auth = new Authorizer();
        using var server = new TouchSocketRelayServer(auth);
        server.Diagnostic += Console.WriteLine;
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var first = new RelayHostWire(); using var second = new RelayHostWire();
        var firstIncoming = Signal<(PeerId, string)>(); var secondIncoming = Signal<(PeerId, string)>();
        var prepared = Signal<PeerId>(); var ready = Signal<PeerId>(); var retired = Signal<PeerId>();
        first.Received += (peer, bytes) => firstIncoming.TrySetResult((peer, Encoding.UTF8.GetString(bytes)));
        second.Received += (peer, bytes) => secondIncoming.TrySetResult((peer, Encoding.UTF8.GetString(bytes)));
        first.PeerPrepared += id => prepared.TrySetResult(id); first.PeerDirectoryReady += id => ready.TrySetResult(id); first.PeerLeft += id => retired.TrySetResult(id);
        await first.ConnectAsync(Options(port, "one"), ticket => Task.FromResult((new PeerId("alice"), "ok-" + ticket)), _ => { });
        using var duplicate = new RelayHostWire();
        await Assert.ThrowsAnyAsync<Exception>(() => duplicate.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("x"), "x")), _ => { }));
        var wrongKey = Options(port, "two"); wrongKey.HostCredential = "wrong";
        using var denied = new RelayHostWire();
        await Assert.ThrowsAnyAsync<Exception>(() => denied.ConnectAsync(wrongKey, _ => Task.FromResult((new PeerId("x"), "x")), _ => { }));
        using var inactive = new RelayClientWire(new PeerId("host-one"));
        await Assert.ThrowsAnyAsync<Exception>(() => inactive.ConnectAsync(Options(port, "one")));
        await second.ConnectAsync(Options(port, "two"), _ => Task.FromResult((new PeerId("bob"), "ok")), _ => { });
        await first.ActivateAsync(); await second.ActivateAsync();
        var wrongScope = Options(port, "two"); wrongScope.Scope = "scope-one";
        using var scoped = new RelayClientWire(new PeerId("host-two"));
        await Assert.ThrowsAnyAsync<Exception>(() => scoped.ConnectAsync(wrongScope));
        using var alice = new RelayClientWire(new PeerId("host-one"));
        using var bob = new RelayClientWire(new PeerId("host-two"));
        await alice.ConnectAsync(Options(port, "one")); await bob.ConnectAsync(Options(port, "two"));
        await Assert.ThrowsAsync<RpcException>(() => alice.SendAsync(new PeerId("host-one"), Encoding.UTF8.GetBytes("early")));
        Assert.Equal("ok-ticket", await alice.RequestAdmissionAsync("ticket"));
        Assert.Equal("ok", await bob.RequestAdmissionAsync("ticket"));
        using var duplicatePeer = new RelayClientWire(new PeerId("host-one"));
        await duplicatePeer.ConnectAsync(Options(port, "one"));
        Assert.Contains("error", await duplicatePeer.RequestAdmissionAsync("second-ticket"));
        await Assert.ThrowsAsync<RpcException>(() => duplicatePeer.SendAsync(new PeerId("host-one"), new byte[] { 1 }));
        await alice.NotifyPreparedAsync(); await alice.NotifyDirectoryReadyAsync();
        Assert.Equal("alice", (await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        Assert.Equal("alice", (await ready.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        await alice.SendAsync(new PeerId("host-one"), Encoding.UTF8.GetBytes("alpha"));
        await bob.SendAsync(new PeerId("host-two"), Encoding.UTF8.GetBytes("beta"));
        var fromAlice = await firstIncoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var fromBob = await secondIncoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("alice", "alpha"), (fromAlice.Item1.Value, fromAlice.Item2));
        Assert.Equal(("bob", "beta"), (fromBob.Item1.Value, fromBob.Item2));
        await Assert.ThrowsAsync<RpcException>(() => alice.SendAsync(new PeerId("host-two"), new byte[] { 1 }));
        var clientReceived = Signal<(PeerId, string)>(); alice.Received += (id, data) => clientReceived.TrySetResult((id, Encoding.UTF8.GetString(data)));
        await first.SendAsync(new PeerId("alice"), Encoding.UTF8.GetBytes("pong"));
        var fromHost = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("host-one", "pong"), (fromHost.Item1.Value, fromHost.Item2));
        await Assert.ThrowsAsync<RpcException>(() => first.SendAsync(new PeerId("bob"), new byte[] { 1 }));
        await alice.NotifyLeavingAsync();
        Assert.Equal("alice", (await retired.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        await Eventually(() => server.GetStatistics().Clients == 1);
        Assert.Equal(3, server.GetStatistics().ForwardedMessages);
        Assert.Equal(13, server.GetStatistics().ForwardedBytes); // alpha(5), beta(4), pong(4)
        first.Dispose();
        await Eventually(() => server.GetStatistics().Rooms == 1 && auth.Closed.ContainsKey("one"));
        Assert.True(bob.IsConnected);
        second.Dispose();
        await Eventually(() => server.GetStatistics().Rooms == 0 && auth.Closed.ContainsKey("two"));
    }

    [Fact]
    public async Task RelayMemoryLanePreservesPhysicalSenderAndOnlyOneReceiveEvent()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        int legacy = 0;
        host.Received += (_, _) => Interlocked.Increment(ref legacy);
        var received = Signal<string>();
        host.MemoryReceived += (peer, bytes) => received.TrySetResult(peer.Value + ":" + Encoding.UTF8.GetString(bytes.Span));
        var source = Encoding.UTF8.GetBytes("relay-lease");
        await client.SendMemoryAsync(new PeerId("host-one"), source);
        Array.Clear(source);
        Assert.Equal("alice:relay-lease", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref legacy));
        var reply = Signal<string>(); client.MemoryReceived += (peer, bytes) => reply.TrySetResult(peer.Value + ":" + Encoding.UTF8.GetString(bytes.Span));
        await host.SendMemoryAsync(new PeerId("alice"), Encoding.UTF8.GetBytes("relay-return"));
        Assert.Equal("host-one:relay-return", await reply.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var opaque = Signal<byte[]>(); host.MemoryReceived += (_, bytes) => opaque.TrySetResult(bytes.ToArray());
        byte[] numericCoreFrame = { 0, 128, 255, 0, 37, 1 };
        await client.SendMemoryAsync(new PeerId("host-one"), numericCoreFrame);
        Assert.Equal(numericCoreFrame, await opaque.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, server.GetStatistics().ForwardedMessages);
    }

    [Fact]
    public async Task RelayLossAndLimitsBreakClients()
    {
        int port = FreePort(); var auth = new Authorizer();
        using var server = new TouchSocketRelayServer(auth);
        server.Diagnostic += Console.WriteLine;
        await server.StartAsync(new RelayListenOptions { Port = port, MaxMessageBytes = 4096, MaxQueuedBytesPerConnection = 4101, MaxQueuedMessagesPerConnection = 1 });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        var left = Signal<PeerId>(); client.PeerLeft += id => left.TrySetResult(id);
        server.Dispose();
        Assert.Equal("host-one", (await left.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        await Eventually(() => !host.IsConnected && !client.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(new PeerId("host-one"), new byte[] { 1 }));
    }

    [Fact]
    public async Task OversizedGameplayIsRejectedByServer()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port, MaxMessageBytes = 4096 });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await client.SendAsync(new PeerId("host-one"), new byte[4096]); // 4100-byte envelope
        await Eventually(() => !client.IsConnected && server.GetStatistics().Clients == 0);
        Assert.Equal(0, server.GetStatistics().ForwardedMessages);
    }

    [Fact]
    public async Task LateHostApprovalIsAbortedAndNeverAdmitted()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        var slow = Signal<(PeerId Peer, string Response)>();
        var aborted = Signal<PeerId>();
        await host.ConnectAsync(Options(port, "one"), _ => slow.Task, _ => { }, id => aborted.TrySetResult(id));
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Contains("error", await client.RequestAdmissionAsync("slow").WaitAsync(TimeSpan.FromSeconds(8)));
        slow.SetResult((new PeerId("late"), "ok"));
        Assert.Equal("late", (await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        Assert.Equal(0, server.GetStatistics().Clients);
        await Assert.ThrowsAsync<RpcException>(() => client.SendAsync(new PeerId("host-one"), new byte[] { 1 }));
    }

    [Fact]
    public async Task HostDisconnectClosesAdmittedClientExactlyOnce()
    {
        int port = FreePort(); var auth = new Authorizer();
        using var server = new TouchSocketRelayServer(auth);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        int left = 0; client.PeerLeft += _ => Interlocked.Increment(ref left);
        host.Dispose();
        await Eventually(() => !client.IsConnected && server.GetStatistics().Rooms == 0 && auth.Closed.ContainsKey("one") && Volatile.Read(ref left) == 1);
        client.Dispose();
        Assert.Equal(1, Volatile.Read(ref left));
    }

    [Fact]
    public async Task DmtpVerifyTokenAndProtocol220RejectUnregisteredGameplay()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port, VerifyToken = "test-relay-verified" });
        using var wrong = new TcpDmtpClient();
        await wrong.SetupAsync(new TouchSocketConfig().SetDmtpOption(o => o.VerifyToken = "wrong")
            .SetRemoteIPHost("127.0.0.1:" + port));
        await Assert.ThrowsAnyAsync<Exception>(() => wrong.ConnectAsync()); // DMTP handshake, before relay frames
        using var verified = new TcpDmtpClient();
        await verified.SetupAsync(new TouchSocketConfig().SetDmtpOption(o => o.VerifyToken = "test-relay-verified")
            .SetRemoteIPHost("127.0.0.1:" + port));
        await verified.ConnectAsync();
        Assert.True(verified.Online);
        await verified.DmtpActor.SendAsync(220, new byte[] { 9, 0, 0, 0, 0 }); // DMTP relay protocol; gameplay before registration
        await Eventually(() => server.GetStatistics().RejectedMessages > 0 && !verified.Online);
        Assert.Equal(0, server.GetStatistics().Rooms);
        Assert.Equal(0, server.GetStatistics().ForwardedMessages);
    }

    [Fact]
    public async Task CancelledAdmissionReleasesPendingSocketImmediately()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        var blocked = Signal<(PeerId Peer, string Response)>();
        await host.ConnectAsync(Options(port, "one"), _ => blocked.Task, _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<Exception>(() => client.RequestAdmissionAsync("ticket", cancellation.Token));
        await Eventually(() => server.GetStatistics().PendingAdmissions == 0);
        blocked.TrySetResult((new PeerId("late"), "ok"));
        await Eventually(() => server.GetStatistics().Clients == 0);
    }

    [Fact]
    public async Task WriterOverflowClosesSocketAndFailsInflightSend()
    {
        var entered = Signal<bool>(); var blocked = Signal<bool>();
        int closes = 0;
        using var socket = new RelaySocket(async bytes => { entered.TrySetResult(true); await blocked.Task; },
            () => Interlocked.Increment(ref closes), 128, 1, 129);
        var inflight = socket.Send(RelayOp.Data, new byte[100]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var overflow = Assert.Throws<RpcException>(() => { _ = socket.Send(RelayOp.Data, new byte[] { 2 }); });
        Assert.Equal(RpcError.LimitExceeded, overflow.Error);
        // A closed socket may reject queued writes immediately, but MUST retain the
        // active rental and defer its Task until the underlying DMTP send stops reading it.
        Assert.False(inflight.IsCompleted);
        Assert.Equal(1, socket.PoolOutstanding);
        Assert.Equal(1, closes);
        blocked.TrySetResult(true);
        await Assert.ThrowsAsync<RpcException>(() => inflight.WaitAsync(TimeSpan.FromSeconds(3)));
        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, socket.PoolOutstanding);
        Assert.Equal(socket.PoolRents, socket.PoolReturns);
    }

    [Fact]
    public async Task RelayPooledIncomingFrameOwnsBorrowedPluginMemoryUntilReaderDisposes()
    {
        using var socket = new RelaySocket(_ => Task.CompletedTask, () => { }, 4096, 2, 8192);
        var source = new byte[] { (byte)RelayOp.Data, 5, 6, 7 };
        socket.Enqueue(source);
        Array.Clear(source); // original TouchSocket callback buffer has been reused
        using var frame = await socket.Read(CancellationToken.None);
        Assert.Equal(RelayOp.Data, frame.Op);
        Assert.Equal(new byte[] { 5, 6, 7 }, frame.Payload.ToArray());
        socket.Dispose();
        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, socket.PoolOutstanding); // active reader still borrows frame
        frame.Dispose();
        Assert.Equal(0, socket.PoolOutstanding);
        Assert.Equal(socket.PoolRents, socket.PoolReturns);
    }

    [Fact]
    public async Task PooledRelayDataWireRemainsOwnedThroughDelayedAndFaultedUnderlyingSend()
    {
        var entered = Signal<ReadOnlyMemory<byte>>(); var pending = Signal<bool>();
        using var socket = new RelaySocket(async bytes => { entered.TrySetResult(bytes); await pending.Task; }, () => { }, 4096, 2, 8192);
        var caller = Encoding.UTF8.GetBytes("lease-payload");
        var send = socket.SendData(caller, "alice");
        var onWire = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Array.Clear(caller); // queued relay send made one owned copy before returning Task
        Assert.Equal("lease-payload", Encoding.UTF8.GetString(RelayProtocol.ParseData(onWire.Slice(1), true, 4096).Bytes.Span));
        socket.Dispose();
        Assert.False(send.IsCompleted);
        Assert.Equal(1, socket.PoolOutstanding);
        pending.TrySetException(new IOException("simulated DMTP send failure"));
        await Assert.ThrowsAsync<RpcException>(() => send.WaitAsync(TimeSpan.FromSeconds(3)));
        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, socket.PoolOutstanding);
        Assert.Equal(socket.PoolRents, socket.PoolReturns);
    }

    [Fact]
    public async Task QueueOverflowReturnsQueuedRentalsButNotActiveSendBufferEarly()
    {
        var entered = Signal<bool>(); var pending = Signal<bool>();
        using var socket = new RelaySocket(async _ => { entered.TrySetResult(true); await pending.Task; }, () => { }, 128, 2, 258);
        var active = socket.SendData(new byte[60]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = socket.SendData(new byte[60]);
        Assert.Equal(2, socket.PoolOutstanding);
        Assert.Throws<RpcException>(() => { _ = socket.SendData(new byte[60]); });
        await Assert.ThrowsAsync<RpcException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, socket.PoolOutstanding); // only the underlying active write still owns bytes
        Assert.False(active.IsCompleted);
        pending.TrySetResult(true);
        await Assert.ThrowsAsync<RpcException>(() => active.WaitAsync(TimeSpan.FromSeconds(3)));
        await socket.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, socket.PoolOutstanding);
        Assert.Equal(socket.PoolRents, socket.PoolReturns);
    }

    [Fact]
    public async Task HostDisconnectDuringPendingAdmissionInvalidatesRoomAndRequester()
    {
        int port = FreePort(); var auth = new Authorizer();
        using var server = new TouchSocketRelayServer(auth);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        var pending = Signal<(PeerId Peer, string Response)>(); var aborted = Signal<PeerId>(); var started = Signal<bool>();
        await host.ConnectAsync(Options(port, "one"), _ => { started.TrySetResult(true); return pending.Task; }, _ => { }, id => aborted.TrySetResult(id));
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        var admission = client.RequestAdmissionAsync("ticket");
        await Eventually(() => server.GetStatistics().PendingAdmissions == 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        host.Dispose();
        await Eventually(() => server.GetStatistics().Rooms == 0 && server.GetStatistics().PendingAdmissions == 0 && !client.IsConnected && auth.Closed.ContainsKey("one"));
        try { var response = await admission.WaitAsync(TimeSpan.FromSeconds(7)); Assert.Contains("error", response); }
        catch (RpcException ex) { Assert.Equal(RpcError.Disconnected, ex.Error); }
        pending.SetResult((new PeerId("too-late"), "ok"));
        Assert.Equal("too-late", (await aborted.Task.WaitAsync(TimeSpan.FromSeconds(6))).Value);
        Assert.Equal(0, server.GetStatistics().Clients);
    }

    [Fact]
    public async Task RawDmtpClientCannotClaimArbitraryPhysicalSender()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire();
        int gameplay = 0;
        host.Received += (_, _) => Interlocked.Increment(ref gameplay);
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync();
        using var rogue = new TcpDmtpClient();
        var selected = Signal<bool>(); var admitted = Signal<bool>();
        await rogue.SetupAsync(new TouchSocketConfig()
            .ConfigurePlugins(plugins => plugins.AddDmtpReceivedPlugin((IDmtpActorObject actor, DmtpMessageEventArgs e) =>
            {
                if (ReferenceEquals(actor, rogue) && e.DmtpMessage.ProtocolFlags == 220)
                {
                    var message = e.DmtpMessage.Memory.ToArray();
                    if (message.Length > 1 && message[0] == 4) selected.TrySetResult(message[1] != 0);
                    if (message.Length > 1 && message[0] == 8) admitted.TrySetResult(message[1] != 0);
                }
                return Task.CompletedTask;
            }))
            .SetDmtpOption(o => o.VerifyToken = "bitkit-relay-v1")
            .SetRemoteIPHost("127.0.0.1:" + port));
        await rogue.ConnectAsync();
        await rogue.DmtpActor.SendAsync(220, DmtpFrame(2, w => { w.Write("bitkit-relay-v1"); w.Write("one"); w.Write("scope-one"); w.Write("host-one"); }));
        Assert.True(await selected.Task.WaitAsync(TimeSpan.FromSeconds(4)));
        await rogue.DmtpActor.SendAsync(220, DmtpFrame(5, w => w.Write("ticket")));
        Assert.True(await admitted.Task.WaitAsync(TimeSpan.FromSeconds(4)));
        // The payload format contains no Source. Smuggling a claimed source is malformed,
        // so the relay must close this selected and admitted physical socket.
        await rogue.DmtpActor.SendAsync(220, DmtpFrame(9, w => { w.Write(1); w.Write((byte)42); w.Write("host-one"); }));
        await Eventually(() => server.GetStatistics().Clients == 0 && !rogue.Online);
        Assert.Equal(0, Volatile.Read(ref gameplay));
        Assert.Equal(0, server.GetStatistics().ForwardedMessages);
    }

    [Fact]
    public async Task HostCloseWhilePublishingClosesAfterLatePublish()
    {
        int port = FreePort(); var auth = new DelayedActivation();
        using var server = new TouchSocketRelayServer(auth);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire();
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        var activating = host.ActivateAsync();
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        host.Dispose();
        await Eventually(() => server.GetStatistics().Rooms == 0);
        Assert.Equal(0, Volatile.Read(ref auth.Closes));
        auth.Publish.SetResult(true);
        await Eventually(() => Volatile.Read(ref auth.Closes) == 1);
        await Assert.ThrowsAnyAsync<Exception>(() => activating);
    }

    [Fact]
    public async Task ClosingRoomIdCannotRegisterUntilOldCloseCompletes()
    {
        int port = FreePort(); var auth = new DelayedClose();
        using var server = new TouchSocketRelayServer(auth);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var oldHost = new RelayHostWire();
        await oldHost.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await oldHost.ActivateAsync(); oldHost.Dispose();
        await auth.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.GetStatistics().Rooms);
        using var replacement = new RelayHostWire();
        await Assert.ThrowsAnyAsync<Exception>(() => replacement.ConnectAsync(Options(port, "one"),
            _ => Task.FromResult((new PeerId("bob"), "ok")), _ => { }));
        Assert.Equal(1, Volatile.Read(ref auth.Authorizations)); // rejected before external Validate
        auth.FinishClose.SetResult(true);
        // After old CloseRoom has returned, reuse is safe; it cannot close this new owner.
        RelayHostWire? newHost = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (newHost == null)
        {
            var attempt = new RelayHostWire();
            try
            {
                await attempt.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("bob"), "ok")), _ => { });
                newHost = attempt;
            }
            catch { attempt.Dispose(); if (DateTime.UtcNow >= until) throw; await Task.Delay(25); }
        }
        using (newHost)
        {
            Assert.Equal(2, Volatile.Read(ref auth.Authorizations));
            await newHost.ActivateAsync();
            Assert.Equal(1, server.GetStatistics().Rooms);
        }
    }

    [Fact]
    public async Task TlsDoesNotAcceptUntrustedOrWrongHostCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string path = Path.Combine(Path.GetTempPath(), "relay-test-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "test-only-password"));
            int port = FreePort();
            using var server = new TouchSocketRelayServer(new Authorizer());
            await server.StartAsync(new RelayListenOptions { Port = port, CertificatePath = path, CertificatePassword = "test-only-password" });
            foreach (string target in new[] { "localhost", "not-localhost.invalid" })
            {
                var options = Options(port, "one"); options.UseTls = true; options.TlsTargetHost = target;
                using var host = new RelayHostWire();
                await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync(options,
                    _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { }));
                Assert.Equal(0, server.GetStatistics().Rooms);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CommittedAdmissionIsRolledBackExactlyOnceWhenRequesterDrops()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        int committed = 0, aborted = 0, retired = 0;
        host.PeerLeft += _ => Interlocked.Increment(ref retired);
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")),
            _ => { Interlocked.Increment(ref committed); client.Dispose(); }, _ => Interlocked.Increment(ref aborted));
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        try { _ = await client.RequestAdmissionAsync("ticket"); } catch (RpcException) { }
        await Eventually(() => Volatile.Read(ref aborted) + Volatile.Read(ref retired) == 1);
        Assert.Equal(1, Volatile.Read(ref committed));
        Assert.Equal(1, Volatile.Read(ref aborted) + Volatile.Read(ref retired));
        Assert.Equal(0, server.GetStatistics().Clients);
    }

    [Fact]
    public async Task RelayRejectionAfterHostCommitCallsAbortOnlyOnce()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        int committed = 0, aborted = 0, retired = 0;
        host.PeerLeft += _ => Interlocked.Increment(ref retired);
        // The callback commits a PeerId the relay must reject (it belongs to Host).
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("host-one"), "ok")),
            _ => Interlocked.Increment(ref committed), _ => Interlocked.Increment(ref aborted));
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Contains("error", await client.RequestAdmissionAsync("ticket"));
        await Eventually(() => Volatile.Read(ref aborted) == 1);
        Assert.Equal(1, Volatile.Read(ref committed));
        Assert.Equal(0, Volatile.Read(ref retired));
        Assert.Equal(0, server.GetStatistics().Clients);
    }

    [Fact]
    public async Task NormalLeaveKeepsRuntimeWireConnectedUntilCallerDisposes()
    {
        int port = FreePort();
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host-one"));
        var retired = Signal<PeerId>(); host.PeerLeft += id => retired.TrySetResult(id);
        await host.ConnectAsync(Options(port, "one"), _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(Options(port, "one"));
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));

        // A second request must not leave an orphaned _admission TCS behind.
        var slot = typeof(RelayClientWire).GetField("_admission", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAdmissionAsync("again"));
        Assert.Null(slot.GetValue(client));

        using var runtime = new RpcRuntime(NetworkRole.Client, "scope-one", new PeerId("alice"), new PeerId("host-one"), client);
        var boundKey = new TargetKey("leave-bound-state");
        runtime.Bind(boundKey, new object());
        int unhandled = 0, changed = 0, lost = 0;
        runtime.UnhandledDispatch += _ => Interlocked.Increment(ref unhandled);
        runtime.MembersChanged += _ =>
        {
            Interlocked.Increment(ref changed);
            runtime.RemoveTarget(boundKey); // mirrors Arena's RemoveHealth on MembersChanged
        };
        client.PeerLeft += _ => Interlocked.Increment(ref lost);

        await client.NotifyLeavingAsync();
        Assert.Equal("alice", (await retired.Task.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        Assert.True(client.IsConnected);
        Assert.True(runtime.IsConnected);
        Assert.Equal(0, Volatile.Read(ref unhandled));
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.Equal(0, Volatile.Read(ref lost));
        await Assert.ThrowsAsync<RpcException>(() => client.SendAsync(new PeerId("host-one"), new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAdmissionAsync("reuse"));
        Assert.Null(slot.GetValue(client));

        runtime.Dispose(); // unsubscribes PeerLeft before the caller closes the wire
        client.Dispose();
        Assert.Equal(0, Volatile.Read(ref unhandled));
        Assert.Equal(0, Volatile.Read(ref lost)); // normal leave is not Host loss
    }
}
