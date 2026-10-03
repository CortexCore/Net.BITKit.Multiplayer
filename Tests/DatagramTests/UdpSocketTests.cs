using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;
using TouchSocket.Core;
using TouchSocket.Sockets;
using Xunit;
using ITransport = BITKit.Multiplayer.ITransport;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DatagramTests;

public sealed class UdpSocketTests
{
    internal static int Port()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
            finally { tcp.Stop(); }
        }
        throw new InvalidOperationException("Could not reserve a port available to both TCP and UDP.");
    }
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(25);
        Assert.True(predicate());
    }
    private static TaskCompletionSource<string> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task DirectMemoryLaneBorrowsReceiveWithoutDoubleDeliveringLegacyEvent()
    {
        int port = Port(); var hostId = new PeerId("host"); var peer = new PeerId("alice");
        using var host = new TouchSocketHostWire(); using var client = new TouchSocketClientWire(hostId);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((peer, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        int legacy = 0;
        host.Received += (_, _) => Interlocked.Increment(ref legacy);
        var incoming = Signal();
        Action<PeerId, ReadOnlyMemory<byte>> handler = (sender, bytes) =>
        {
            Assert.Equal(peer, sender);
            incoming.TrySetResult(Encoding.UTF8.GetString(bytes.Span)); // decode before callback returns
        };
        host.MemoryReceived += handler;
        var caller = Encoding.UTF8.GetBytes("borrowed-direct");
        await client.SendMemoryAsync(hostId, caller);
        caller.AsSpan().Clear();
        Assert.Equal("borrowed-direct", await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref legacy));
        host.MemoryReceived -= handler;
        var fallback = Signal(); host.Received += (_, bytes) => fallback.TrySetResult(Encoding.UTF8.GetString(bytes));
        await client.SendMemoryAsync(hostId, Encoding.UTF8.GetBytes("legacy-fallback"));
        Assert.Equal("legacy-fallback", await fallback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var clientMemory = Signal(); client.MemoryReceived += (sender, bytes) => clientMemory.TrySetResult(sender.Value + ":" + Encoding.UTF8.GetString(bytes.Span));
        await host.SendMemoryAsync(peer, Encoding.UTF8.GetBytes("host-response"));
        Assert.Equal("host:host-response", await clientMemory.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private static UdpLane Lane(object wire) => Assert.IsType<UdpLane>(wire.GetType().GetField("_udp", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wire));
    private static async Task AssertReturned(params UdpLane[] lanes)
    {
        await Task.WhenAll(lanes.Select(lane => lane.TransportCompletion)).WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => lanes.All(lane => lane.PoolOutstanding == 0 && lane.InflightDataSends == 0));
        foreach (var lane in lanes)
        {
            Assert.Equal(lane.PoolRents, lane.PoolReturns);
            Assert.Equal(0, lane.PoolOutstandingBytes);
            Assert.InRange(lane.PeakPoolOutstandingBytes, 0, (long)lane.PeakPoolOutstanding * 2048);
        }
    }
    private sealed class TrackingFactory : ITransportFactory
    {
        private readonly ITransportFactory _inner;
        private readonly object _gate = new object();
        private readonly List<TrackingTransport> _created = new List<TrackingTransport>();
        internal bool DenyCapability, FailStart;
        internal TrackingFactory(ITransportFactory inner) => _inner = inner;
        public string Name => "spy-" + _inner.Name;
        public ITransport Create()
        {
            var endpoint = new TrackingTransport(_inner.Create(), DenyCapability, FailStart);
            lock (_gate) _created.Add(endpoint);
            return endpoint;
        }
        internal TrackingTransport[] Created { get { lock (_gate) return _created.ToArray(); } }
    }
    private sealed class TrackingTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly bool _deny, _fail;
        internal int Starts, Disposes;
        internal TransportOptions? Options;
        internal TrackingTransport(ITransport inner, bool deny, bool fail)
        {
            _inner = inner; _deny = deny; _fail = fail;
            _inner.Received += (source, memory, delivery) => Received?.Invoke(source, memory, delivery);
            _inner.Faulted += ex => Faulted?.Invoke(ex);
        }
        public TransportCapabilities Capabilities => _deny ? TransportCapabilities.ReliableOrdered : _inner.Capabilities;
        public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public Task Completion => _inner.Completion;
        public event Action<EndPoint, ReadOnlyMemory<byte>, RpcDelivery>? Received;
        public event Action<Exception>? Faulted;
        internal void Inject(EndPoint source, ReadOnlyMemory<byte> packet, RpcDelivery delivery) => Received?.Invoke(source, packet, delivery);
        public async Task StartAsync(TransportOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts); Options = options;
            if (_fail) throw new InvalidOperationException("test factory endpoint startup failed");
            await _inner.StartAsync(options, cancellationToken);
        }
        public ValueTask SendAsync(EndPoint destination, ReadOnlyMemory<byte> payload, RpcDelivery delivery = RpcDelivery.Unreliable,
            CancellationToken cancellationToken = default) => _inner.SendAsync(destination, payload, delivery, cancellationToken);
        public TransportStatistics GetStatistics() => _inner.GetStatistics();
        public Task StopAsync() => _inner.StopAsync();
        public void Dispose() { if (Interlocked.Exchange(ref Disposes, 1) == 0) _inner.Dispose(); }
    }
    private static async Task AssertFactoryStopped(TrackingFactory factory, int expected)
    {
        var created = factory.Created;
        Assert.Equal(expected, created.Length);
        await Task.WhenAll(created.Select(c => c.Completion)).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.All(created, c => Assert.Equal(1, c.Disposes));
    }

    [Fact]
    public async Task InjectedNativeFactoryDirectRebindCreatesAndStopsDistinctEndpoints()
    {
        int port = Port(); var hostId = new PeerId("host"); var peer = new PeerId("alice");
        var factory = new TrackingFactory(new UdpTransportFactory());
        using var host = new TouchSocketHostWire(factory);
        using var client = new TouchSocketClientWire(hostId, factory);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((peer, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await Until(() => host.IsUnreliableReady(peer) && client.IsUnreliableReady(hostId));
        var created = factory.Created;
        Assert.Equal(2, created.Length);
        Assert.Equal(port, ((IPEndPoint)created[0].LocalEndPoint!).Port);
        int oldPort = ((IPEndPoint)created[1].LocalEndPoint!).Port;
        await client.RebindDatagramsAsync();
        created = factory.Created;
        Assert.Equal(3, created.Length);
        Assert.NotSame(created[1], created[2]);
        Assert.NotEqual(oldPort, ((IPEndPoint)created[2].LocalEndPoint!).Port);
        Assert.All(created, c => { Assert.Equal(1, c.Starts); Assert.Equal(1200, c.Options!.MaxPacketBytes); Assert.Equal(64, c.Options.MaxConcurrentSends); });
        await created[1].Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, created[1].Disposes);
        await client.NotifyLeavingAsync(); client.Dispose(); host.Dispose();
        await AssertFactoryStopped(factory, 3);
    }

    [Fact]
    public async Task InjectedNativeFactoryRelayRebindAndRoomLossStopEveryEndpoint()
    {
        int port = Port(); var factory = new TrackingFactory(new UdpTransportFactory());
        var options = new RelayConnectOptions { Port = port, RoomId = "room", Scope = "scope", HostPeerId = new PeerId("host"), HostCredential = "secret" };
        using var server = new TouchSocketRelayServer(new Authorizer(), factory);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(factory); using var client = new RelayClientWire(new PeerId("host"), factory);
        await host.ConnectAsync(options, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(options);
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await Until(() => host.IsUnreliableReady(new PeerId("alice")) && client.IsUnreliableReady(new PeerId("host")));
        Assert.Equal(3, factory.Created.Length); // server listener, reverse host, client
        var oldHostPort = ((IPEndPoint)factory.Created[1].LocalEndPoint!).Port;
        var oldClientPort = ((IPEndPoint)factory.Created[2].LocalEndPoint!).Port;
        await host.RebindDatagramsAsync(); await client.RebindDatagramsAsync();
        var created = factory.Created;
        Assert.Equal(5, created.Length);
        Assert.NotEqual(oldHostPort, ((IPEndPoint)created[3].LocalEndPoint!).Port);
        Assert.NotEqual(oldClientPort, ((IPEndPoint)created[4].LocalEndPoint!).Port);
        await Task.WhenAll(created[1].Completion, created[2].Completion).WaitAsync(TimeSpan.FromSeconds(8));
        host.Dispose();
        await Until(() => !client.IsConnected);
        client.Dispose(); server.Dispose();
        await AssertFactoryStopped(factory, 5);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsupportedOrFailedInjectedEndpointNeverStartsReliableRoom(bool unsupported, bool failsStart)
    {
        int port = Port(); var factory = new TrackingFactory(new UdpTransportFactory()) { DenyCapability = unsupported, FailStart = failsStart };
        using var host = new TouchSocketHostWire(factory);
        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(port, "verify"));
        Assert.False(host.IsConnected);
        Assert.Single(factory.Created);
        Assert.Equal(unsupported ? 0 : 1, factory.Created[0].Starts);
        await AssertFactoryStopped(factory, 1);
    }
    [Fact]
    public async Task NonUdpDeliveryFromTransportCannotEnterAuthenticatedUdpLane()
    {
        var factory = new TrackingFactory(new UdpTransportFactory());
        using var lane = new UdpLane(true, (_, _, _) => throw new Exception("unexpected receive"), factory: factory);
        await lane.Start(0, IPAddress.Loopback);
        factory.Created[0].Inject(new IPEndPoint(IPAddress.Loopback, 12345), new byte[1200], RpcDelivery.Reliable);
        Assert.Equal(1, lane.Statistics().RejectedDatagrams);
        lane.Dispose(); await AssertFactoryStopped(factory, 1);
    }
    [Fact]
    public async Task FailedNewEndpointDuringRebindDoesNotReplaceWorkingEndpointOrReliableRoom()
    {
        int port = Port(); var factory = new TrackingFactory(new UdpTransportFactory());
        var hostId = new PeerId("host"); var peer = new PeerId("alice");
        using var host = new TouchSocketHostWire(factory); using var client = new TouchSocketClientWire(hostId, factory);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((peer, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await Until(() => host.IsUnreliableReady(peer) && client.IsUnreliableReady(hostId));
        factory.FailStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RebindDatagramsAsync());
        Assert.Equal(3, factory.Created.Length);
        await factory.Created[2].Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, factory.Created[2].Disposes);
        Assert.True(client.IsConnected && host.IsConnected && client.IsUnreliableReady(hostId));
        var received = Signal(); host.Received += (_, bytes) => received.TrySetResult(Encoding.UTF8.GetString(bytes));
        await client.SendAsync(hostId, Encoding.UTF8.GetBytes("still reliable"));
        Assert.Equal("still reliable", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        factory.FailStart = false;
        await client.RebindDatagramsAsync();
        await client.NotifyLeavingAsync(); client.Dispose(); host.Dispose();
        await AssertFactoryStopped(factory, 4);
    }

    [Fact]
    public async Task ExplicitLegacyTouchSocketFactoryDirectBindDataAndRebind()
    {
        int port = Port(); var factory = new TrackingFactory(new TouchSocketUdpTransportFactory());
        var hostId = new PeerId("host"); var clientId = new PeerId("alice");
        using var host = new TouchSocketHostWire(factory);
        using var client = new TouchSocketClientWire(hostId, factory);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((clientId, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await Until(() => host.IsUnreliableReady(clientId) && client.IsUnreliableReady(hostId));
        var received = Signal(); host.UnreliableReceived += (_, bytes) => received.TrySetResult(Encoding.UTF8.GetString(bytes.Span));
        await client.SendUnreliableAsync(hostId, Encoding.UTF8.GetBytes("legacy-direct"));
        Assert.Equal("legacy-direct", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await client.RebindDatagramsAsync();
        Assert.Equal(3, factory.Created.Length);
        await client.NotifyLeavingAsync(); client.Dispose(); host.Dispose();
        await AssertFactoryStopped(factory, 3);
    }

    [Fact]
    public async Task ExplicitLegacyTouchSocketFactoryRelayBindAndShutdown()
    {
        int port = Port(); var factory = new TrackingFactory(new TouchSocketUdpTransportFactory());
        var options = new RelayConnectOptions { Port = port, RoomId = "room", Scope = "scope", HostPeerId = new PeerId("host"), HostCredential = "secret" };
        using var server = new TouchSocketRelayServer(new Authorizer(), factory);
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(factory); using var client = new RelayClientWire(new PeerId("host"), factory);
        await host.ConnectAsync(options, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(options);
        Assert.Equal("ok", await client.RequestAdmissionAsync("ticket"));
        await Until(() => host.IsUnreliableReady(new PeerId("alice")) && client.IsUnreliableReady(new PeerId("host")));
        var received = Signal(); host.UnreliableReceived += (_, bytes) => received.TrySetResult(Encoding.UTF8.GetString(bytes.Span));
        await client.SendUnreliableAsync(new PeerId("host"), Encoding.UTF8.GetBytes("legacy-relay"));
        Assert.Equal("legacy-relay", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, factory.Created.Length);
        client.Dispose(); host.Dispose(); server.Dispose();
        await AssertFactoryStopped(factory, 3);
    }
    private static byte[] Packet(string credential, ulong nonce, byte kind, string claim, byte[] data)
    {
        var name = Encoding.UTF8.GetBytes(claim);
        var body = new byte[1 + name.Length + data.Length]; body[0] = (byte)name.Length;
        Array.Copy(name, 0, body, 1, name.Length); Array.Copy(data, 0, body, 1 + name.Length, data.Length);
        return RawPacket(credential, nonce, kind, body);
    }
    private static byte[] RawPacket(string credential, ulong nonce, byte kind, byte[] body)
    {
        var secret = Convert.FromBase64String(credential);
        var wire = new byte[27 + body.Length + 32]; wire[0] = kind;
        Array.Copy(secret, 0, wire, 1, 16);
        Array.Copy(BitConverter.GetBytes(nonce), 0, wire, 17, 8);
        Array.Copy(body, 0, wire, 27, body.Length);
        using var hmac = new HMACSHA256(secret[16..]);
        Array.Copy(hmac.ComputeHash(wire, 0, wire.Length - 32), 0, wire, wire.Length - 32, 32);
        return wire;
    }

    [Fact]
    public async Task DelayedAndFaultedUdpSendKeepsBorrowedWireRentalUntilTaskFinishes()
    {
        int port = Port(); var peer = new PeerId("peer"); var credential = UdpLane.NewCredential();
        using var server = new UdpLane(true, (_, _, _) => { });
        using var client = new UdpLane(false, (_, _, _) => { });
        await server.Start(port, IPAddress.Loopback);
        await client.Start(0, IPAddress.Any, new IPEndPoint(IPAddress.Loopback, port));
        server.Install(peer, credential);
        await client.Wait(client.Install(peer, credential), CancellationToken.None);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> inFlightWire = default;
        client.SendOverride = (_, _, bytes) => { inFlightWire = bytes; return pending.Task; };
        var caller = new byte[] { 0x52, 0xA5 };
        var sending = client.Send(peer, caller);
        caller.AsSpan().Clear(); // copied before first await; caller still owns its memory
        Assert.False(sending.IsCompleted);
        Assert.Equal(2, client.PoolOutstanding); // owned body + wire, not just a task tracking a returned buffer
        Assert.Equal(0x52, inFlightWire.Span[28]);
        Assert.Equal(0xA5, inFlightWire.Span[29]);
        pending.TrySetException(new InvalidOperationException("simulated TouchSocket write failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sending);
        client.SendOverride = null;
        await client.Send(peer, new byte[] { 7 }); // real TouchSocket still usable after send fault
        client.Dispose(); server.Dispose();
        await AssertReturned(client, server);
        Assert.True(client.PoolRents >= 4);
    }

    [Fact]
    public async Task StalledUdpSendsCapAt64AndReturnAllPooledBuffersAfterFault()
    {
        int port = Port(); var peer = new PeerId("peer"); var credential = UdpLane.NewCredential();
        using var server = new UdpLane(true, (_, _, _) => { });
        using var client = new UdpLane(false, (_, _, _) => { });
        await server.Start(port, IPAddress.Loopback);
        await client.Start(0, IPAddress.Any, new IPEndPoint(IPAddress.Loopback, port));
        server.Install(peer, credential);
        await client.Wait(client.Install(peer, credential), CancellationToken.None);
        var stalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SendOverride = (_, _, _) => stalled.Task;
        var calls = Enumerable.Range(0, 200).Select(_ => client.Send(peer, new byte[900])).ToArray();
        Assert.Equal(64, client.InflightDataSends);
        Assert.Equal(128, client.PoolOutstanding); // 64 owned payload copies + 64 owned UDP wire buffers
        Assert.InRange(client.PeakPoolOutstanding, 128, 132); // at most four concurrent controls per grant
        Assert.True(client.Statistics().DroppedDatagrams >= 136);
        stalled.TrySetException(new InvalidOperationException("stalled UDP writer failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(calls));
        client.Dispose(); server.Dispose();
        await AssertReturned(client, server);
    }

    [Fact]
    public async Task ReusedMacMatchesReferenceWhileSigningAndVerifyingConcurrently()
    {
        var peer = new PeerId("peer"); var credential = UdpLane.NewCredential();
        var factory = new TrackingFactory(new UdpTransportFactory());
        var payload = new byte[] { 9, 18, 27 }; const string claim = "peer-目标";
        int received = 0, signed = 0, corrupt = 0;
        using var server = new UdpLane(true, (sender, bytes, name) =>
        {
            if (!sender.Equals(peer) || name != claim || !bytes.Span.SequenceEqual(payload)) Interlocked.Increment(ref corrupt);
            Interlocked.Increment(ref received);
        }, factory: factory);
        using var client = new UdpLane(false, (_, _, _) => { }, factory: factory);
        await server.Start(0, IPAddress.Loopback);
        await client.Start(0, IPAddress.Loopback, (IPEndPoint)factory.Created[0].LocalEndPoint!);
        var oldGrant = server.Install(peer, credential);
        await client.Wait(client.Install(peer, credential), CancellationToken.None);
        server.SendOverride = (_, _, bytes) =>
        {
            ulong nonce = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span.Slice(17, 8));
            Assert.Equal(RawPacket(credential, nonce, bytes.Span[0], bytes.Slice(27, bytes.Length - 59).ToArray()), bytes.ToArray());
            if (bytes.Span[0] == 6) Interlocked.Increment(ref signed);
            return Task.CompletedTask;
        };
        var source = factory.Created[1].LocalEndPoint!;
        var incoming = factory.Created[0];
        var signers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 128; i++) await server.Send(peer, payload, claim);
        })).ToArray();
        var verifier = Task.Run(() =>
        {
            for (ulong i = 1000; i < 1128; i++) incoming.Inject(source, Packet(credential, i, 6, claim, payload), RpcDelivery.Unreliable);
        });
        await Task.WhenAll(signers.Append(verifier)).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(512, signed); Assert.Equal(128, received); Assert.Equal(0, corrupt);
        // Isolate framing + HMAC from socket I/O, after verifying emitted bytes above.
        // This is a synchronous test seam, NOT a claim of zero-GC real networking.
        server.SendOverride = (_, _, _) => Task.CompletedTask;
        for (int i = 0; i < 128; i++) await server.Send(peer, payload);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            var completedSend = server.Send(peer, payload);
            if (!completedSend.IsCompletedSuccessfully) throw new InvalidOperationException("Allocation fixture must remain synchronous.");
            await completedSend;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        var oldMac = oldGrant.Mac!;
        server.Install(peer, UdpLane.NewCredential());
        Assert.True(oldGrant.Revoked); Assert.Null(oldGrant.Mac);
        Assert.Throws<ObjectDisposedException>(() => oldMac.ComputeHash(payload));
        incoming.Inject(source, Packet(credential, 2000, 6, claim, payload), RpcDelivery.Unreliable);
        Assert.Equal(128, received);
        client.Dispose(); server.Dispose(); await AssertReturned(client, server);
    }

    [Fact]
    public async Task RevocationDisposesMacButKeepsPendingFrameUntilSendEndsAndNonceCannotWrap()
    {
        var factory = new TrackingFactory(new UdpTransportFactory());
        var peer = new PeerId("peer"); var credential = UdpLane.NewCredential();
        using var server = new UdpLane(true, (_, _, _) => { }, factory: factory);
        using var client = new UdpLane(false, (_, _, _) => { });
        await server.Start(0, IPAddress.Loopback);
        await client.Start(0, IPAddress.Loopback, (IPEndPoint)factory.Created[0].LocalEndPoint!);
        server.Install(peer, credential);
        var grant = client.Install(peer, credential);
        await client.Wait(grant, CancellationToken.None);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> held = default;
        client.SendOverride = (_, _, bytes) => { held = bytes; return pending.Task; };
        var send = client.Send(peer, new byte[] { 17, 99 });
        var expected = held.ToArray();
        var mac = grant.Mac!;
        client.Remove(peer);
        Assert.Null(grant.Mac);
        Assert.Throws<ObjectDisposedException>(() => mac.ComputeHash(new byte[1]));
        Assert.False(send.IsCompleted); Assert.Equal(2, client.PoolOutstanding);
        Assert.Equal(expected, held.ToArray());
        await Assert.ThrowsAsync<RpcException>(() => client.Send(peer, new byte[1]));
        pending.SetResult(true); await send;
        Assert.Equal(0, client.PoolOutstanding);
        client.SendOverride = null;
        credential = UdpLane.NewCredential(); server.Install(peer, credential);
        grant = client.Install(peer, credential); await client.Wait(grant, CancellationToken.None);
        grant.Next = ulong.MaxValue;
        await Assert.ThrowsAsync<RpcException>(() => client.Send(peer, new byte[1]));
        Assert.True(grant.Revoked); Assert.Null(grant.Mac); Assert.False(client.Ready(peer));
        client.Dispose(); server.Dispose(); await AssertReturned(client, server);
    }

    [Fact]
    public async Task DirectRealUdpCyclesAndEndpointRotationReturnAllLaneRentals()
    {
        int port = Port(), received = 0, corrupt = 0; var hostId = new PeerId("host"); var peer = new PeerId("alice");
        using var host = new TouchSocketHostWire(); using var client = new TouchSocketClientWire(hostId);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((peer, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("join"));
        await Until(() => host.IsUnreliableReady(peer) && client.IsUnreliableReady(hostId));
        var hostLane = Lane(host); var first = Lane(client);
        host.UnreliableReceived += (_, bytes) =>
        {
            if (bytes.Length != 2 || (bytes.Span[0] ^ bytes.Span[1]) != 255) Interlocked.Increment(ref corrupt);
            Interlocked.Increment(ref received);
        };
        var caller = new byte[2];
        for (int round = 0; round < 2; round++)
        {
            for (int i = 1; i <= 35; i++)
            {
                caller[0] = (byte)i; caller[1] = (byte)(i ^ 255);
                await client.SendUnreliableAsync(hostId, caller);
                caller.AsSpan().Clear(); // legal after returned Task; wire cannot still borrow caller
                await Task.Delay(2);
            }
            await Until(() => Volatile.Read(ref received) >= (round + 1) * 35);
            await Until(() => Lane(client).PoolOutstanding == 0 && Lane(client).InflightDataSends == 0);
            if (round == 0) await client.RebindDatagramsAsync();
        }
        var second = Lane(client);
        await client.NotifyLeavingAsync(); client.Dispose(); host.Dispose();
        await AssertReturned(first, second, hostLane);
        Assert.Equal(0, Volatile.Read(ref corrupt));
        Assert.InRange(first.PeakPoolOutstanding, 1, 132); // 64 data sends * 2 rentals + 4 controls
        Assert.InRange(second.PeakPoolOutstanding, 1, 132);
    }

    [Fact]
    public async Task RelayRealUdpRebindAndShutdownReturnForwardedReceiveRentals()
    {
        int port = Port(), received = 0, corrupt = 0;
        var options = new RelayConnectOptions { Port = port, RoomId = "room", Scope = "scope", HostPeerId = new PeerId("host"), HostCredential = "secret" };
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host"));
        await host.ConnectAsync(options, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(options);
        Assert.Equal("ok", await client.RequestAdmissionAsync("join"));
        await Until(() => host.IsUnreliableReady(new PeerId("alice")) && client.IsUnreliableReady(new PeerId("host")));
        var serverLane = Lane(server); var oldClient = Lane(client); var oldHost = Lane(host);
        host.UnreliableReceived += (_, bytes) =>
        {
            if (bytes.Length != 2 || (bytes.Span[0] ^ bytes.Span[1]) != 255) Interlocked.Increment(ref corrupt);
            Interlocked.Increment(ref received);
        };
        var caller = new byte[2];
        for (int round = 0; round < 2; round++)
        {
            for (int i = 1; i <= 35; i++)
            {
                caller[0] = (byte)i; caller[1] = (byte)(i ^ 255);
                await client.SendUnreliableAsync(new PeerId("host"), caller);
                caller.AsSpan().Clear();
                await Task.Delay(2);
            }
            await Until(() => Volatile.Read(ref received) >= (round + 1) * 35);
            await Until(() => server.UdpForwardInFlight == 0 && serverLane.InflightDataSends == 0 &&
                Lane(client).InflightDataSends == 0 && Lane(host).InflightDataSends == 0);
            if (round == 0) { await client.RebindDatagramsAsync(); await host.RebindDatagramsAsync(); }
        }
        var newClient = Lane(client); var newHost = Lane(host);
        host.Dispose(); client.Dispose(); server.Dispose();
        await AssertReturned(oldClient, oldHost, newClient, newHost, serverLane);
        Assert.Equal(0, server.UdpForwardInFlight);
        Assert.Equal(0, Volatile.Read(ref corrupt));
        Assert.InRange(serverLane.PeakPoolOutstanding, 1, 200); // 64 forwarding sends: 3 rentals each + controls
        Assert.True(serverLane.PoolRents >= 70); // borrowed receive buffer actually pooled per forwarded packet
    }

    [Fact]
    public async Task ReceiveCallbackDoesNotHoldLaneLockWhileAnotherOwnerReadsStatistics()
    {
        int port = Port(); var otherGate = new object();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var received = new ManualResetEventSlim();
        using var lane = new UdpLane(true, (_, _, _) =>
        {
            entered.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(5)));
            lock (otherGate) received.Set();
        });
        await lane.Start(port, IPAddress.Loopback); var credential = UdpLane.NewCredential();
        lane.Install(new PeerId("peer"), credential);
        using var client = new UdpLane(false, (_, _, _) => { });
        await client.Start(0, IPAddress.Any, new IPEndPoint(IPAddress.Loopback, port));
        await client.Wait(client.Install(new PeerId("peer"), credential), CancellationToken.None);
        await client.Send(new PeerId("peer"), new byte[] { 1 });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        // Lock order is deliberately opposite to Relay.ForwardUdp -> Relay.GetStatistics.
        // The read MUST finish before releasing otherGate to the receive callback.
        var stats = Task.Run(() => { lock (otherGate) { proceed.Set(); return lane.Statistics(); } });
        Assert.True((await Task.WhenAny(stats, Task.Delay(TimeSpan.FromSeconds(2)))) == stats, "Receive held the UDP registry lock across callback");
        Assert.Equal(1, (await stats).ReceivedDatagrams);
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DirectRebindConcurrentWithDataAndStatisticsRemainsResponsive()
    {
        int port = Port(), count = 0; var hostId = new PeerId("host"); var clientId = new PeerId("alice");
        using var host = new TouchSocketHostWire(); using var client = new TouchSocketClientWire(hostId);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((clientId, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("join"));
        await Until(() => host.IsUnreliableReady(clientId) && client.IsUnreliableReady(hostId));
        host.UnreliableReceived += (_, _) => Interlocked.Increment(ref count);
        var traffic = Task.Run(async () =>
        {
            for (int i = 0; i < 150; i++)
            {
                try { await client.SendUnreliableAsync(hostId, new byte[] { 1 }); }
                catch (RpcException) { /* binding rotates; no TCP fallback */ }
                await Task.Delay(2);
            }
        });
        var statistics = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                _ = host.GetDatagramStatistics(); _ = client.GetDatagramStatistics();
                await Task.Delay(1);
            }
        });
        await client.RebindDatagramsAsync();
        await Task.WhenAll(traffic, statistics).WaitAsync(TimeSpan.FromSeconds(8));
        await client.SendUnreliableAsync(hostId, new byte[] { 2 });
        await Until(() => Volatile.Read(ref count) > 0);
        Assert.True(host.IsUnreliableReady(clientId));
    }

    [Fact]
    public async Task AuthenticatedOutOfOrderAcceptedButDuplicateCannotExecuteTwice()
    {
        int port = Port(), received = 0; var credential = UdpLane.NewCredential();
        using var server = new UdpLane(true, (_, _, _) => Interlocked.Increment(ref received));
        await server.Start(port, IPAddress.Loopback); server.Install(new PeerId("peer"), credential);
        var challenge = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sender = new UdpSession();
        await sender.SetupAsync(new TouchSocketConfig().SetBindIPHost(new IPHost("127.0.0.1:0"))
            .ConfigurePlugins(p => p.AddUdpReceivedPlugin((IUdpSessionBase _, UdpReceivedDataEventArgs e) =>
            { if (e.Memory.Span[0] == 2) challenge.TrySetResult(e.Memory.Slice(27, 16).ToArray()); return Task.CompletedTask; })));
        await sender.StartAsync(); var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        await sender.SendAsync(endpoint, RawPacket(credential, 1, 1, Array.Empty<byte>()), CancellationToken.None);
        await sender.SendAsync(endpoint, RawPacket(credential, 2, 3, await challenge.Task.WaitAsync(TimeSpan.FromSeconds(5))), CancellationToken.None);
        await Until(() => server.Ready(new PeerId("peer")));
        // A valid MAC on a malformed frame must not spend a high nonce and starve
        // subsequently delivered, valid lower nonces from the same real endpoint.
        await sender.SendAsync(endpoint, RawPacket(credential, 999, 6, new byte[] { 3 }), CancellationToken.None);
        await Until(() => server.Statistics().RejectedDatagrams > 0);
        await sender.SendAsync(endpoint, Packet(credential, 4, 6, "", new byte[] { 4 }), CancellationToken.None);
        await sender.SendAsync(endpoint, Packet(credential, 3, 6, "", new byte[] { 3 }), CancellationToken.None);
        await Until(() => Volatile.Read(ref received) == 2);
        await sender.SendAsync(endpoint, Packet(credential, 4, 6, "", new byte[] { 4 }), CancellationToken.None);
        await Until(() => server.Statistics().RejectedDatagrams >= 2);
        Assert.Equal(2, Volatile.Read(ref received));
    }

    [Fact]
    public async Task ForgedMacReplayWrongEndpointScopeAndExpiredGrantCannotDeliver()
    {
        int port = Port(), count = 0; var peer = new PeerId("alice");
        using var host = new UdpLane(true, (_, _, _) => Interlocked.Increment(ref count));
        await host.Start(port, IPAddress.Loopback);
        var old = UdpLane.NewCredential(); var hostGrant = host.Install(peer, old);
        using var client = new UdpLane(false, (_, _, _) => { });
        await client.Start(0, IPAddress.Any, new IPEndPoint(IPAddress.Loopback, port));
        var grant = client.Install(peer, old); await client.Wait(grant, CancellationToken.None);
        using var forged = new UdpSession();
        await forged.SetupAsync(new TouchSocketConfig().SetBindIPHost(new IPHost("127.0.0.1:0")));
        await forged.StartAsync(); var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var validFromWrongPort = Packet(old, 900, 6, "", Encoding.UTF8.GetBytes("not delivered"));
        await forged.SendAsync(endpoint, validFromWrongPort, CancellationToken.None);
        var invalidMac = Packet(old, 901, 6, "", new byte[] { 42 }); invalidMac[^1] ^= 1;
        await forged.SendAsync(endpoint, invalidMac, CancellationToken.None);
        await forged.SendAsync(endpoint, Packet(UdpLane.NewCredential(), 902, 6, "", new byte[] { 42 }), CancellationToken.None);
        await forged.SendAsync(endpoint, new byte[1201], CancellationToken.None);
        await client.Send(peer, new byte[] { 5 });
        await Until(() => Volatile.Read(ref count) == 1 && host.Statistics().RejectedDatagrams >= 3);
        hostGrant.LastSeen = DateTime.UtcNow.AddSeconds(-31).Ticks;
        await client.Send(peer, new byte[] { 6 });
        await Until(() => !host.Ready(peer) && host.Statistics().RejectedDatagrams >= 5);
        Assert.Equal(1, Volatile.Read(ref count)); // expired binding cannot be revived by authenticated data
        // Rotate on the reliable plane; the old capability cannot authorize any new endpoint.
        host.Install(peer, UdpLane.NewCredential());
        await forged.SendAsync(endpoint, Packet(old, 1000, 1, "", Array.Empty<byte>()), CancellationToken.None);
        await Until(() => host.Statistics().RejectedDatagrams >= 6);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.False(host.Ready(peer));
    }

    [Fact]
    public async Task DirectBindsSendsPausesRebindsAndKeepsReliableAlive()
    {
        int port = Port(); var hostId = new PeerId("host"); var alice = new PeerId("alice");
        using var host = new TouchSocketHostWire(); using var client = new TouchSocketClientWire(hostId);
        await host.StartAsync(port, "verify", admission: _ => Task.FromResult((alice, "ok")));
        await client.ConnectAsync("127.0.0.1", port, "verify");
        Assert.Equal("ok", await client.RequestAdmissionAsync("join"));
        await Until(() => host.IsUnreliableReady(alice) && client.IsUnreliableReady(hostId));
        var received = Signal(); host.UnreliableReceived += (peer, bytes) => { if (peer.Equals(alice)) received.TrySetResult(Encoding.UTF8.GetString(bytes.Span)); };
        await client.SendUnreliableAsync(hostId, Encoding.UTF8.GetBytes("udp"));
        Assert.Equal("udp", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var reliable = Signal(); host.Received += (_, bytes) => reliable.TrySetResult(Encoding.UTF8.GetString(bytes));
        client.UnreliableEnabled = false;
        await client.SendUnreliableAsync(hostId, Encoding.UTF8.GetBytes("dropped"));
        await client.SendAsync(hostId, Encoding.UTF8.GetBytes("tcp"));
        Assert.Equal("tcp", await reliable.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        client.UnreliableEnabled = true;
        await client.RebindDatagramsAsync();
        Assert.True(client.IsUnreliableReady(hostId));
        await Until(() => host.IsUnreliableReady(alice));
        Assert.Equal(900, client.MaxUnreliablePayloadBytes);
        await Assert.ThrowsAsync<RpcException>(() => client.SendUnreliableAsync(hostId, new byte[901]));
        Assert.InRange(host.GetDatagramStatistics().LargestDatagramBytes, 1, 1200);
        await client.NotifyLeavingAsync();
        await Until(() => !host.IsUnreliableReady(alice));
    }

    private sealed class Authorizer : IRelayRoomAuthorizer
    {
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token) =>
            Task.FromResult<RelayRoomIdentity?>(credential == "secret" ? new RelayRoomIdentity { Scope = "scope", HostPeerId = new PeerId("host") } : null);
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token) => Task.FromResult(true);
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class SharedNamesAuthorizer : IRelayRoomAuthorizer
    {
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token) =>
            Task.FromResult<RelayRoomIdentity?>(new RelayRoomIdentity { Scope = roomId, HostPeerId = new PeerId("same-host") });
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token) => Task.FromResult(true);
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task IdenticalPeerNamesInDifferentRelayRoomsDoNotCrossScope()
    {
        int port = Port();
        using var server = new TouchSocketRelayServer(new SharedNamesAuthorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        var one = new RelayConnectOptions { Port = port, RoomId = "one", Scope = "one", HostPeerId = new PeerId("same-host"), HostCredential = "one" };
        var two = new RelayConnectOptions { Port = port, RoomId = "two", Scope = "two", HostPeerId = new PeerId("same-host"), HostCredential = "two" };
        using var hostOne = new RelayHostWire(); using var hostTwo = new RelayHostWire();
        using var clientOne = new RelayClientWire(new PeerId("same-host")); using var clientTwo = new RelayClientWire(new PeerId("same-host"));
        await hostOne.ConnectAsync(one, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await hostTwo.ConnectAsync(two, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await hostOne.ActivateAsync(); await hostTwo.ActivateAsync();
        await clientOne.ConnectAsync(one); await clientTwo.ConnectAsync(two);
        Assert.Equal("ok", await clientOne.RequestAdmissionAsync("first"));
        Assert.Equal("ok", await clientTwo.RequestAdmissionAsync("second"));
        await Until(() => clientOne.IsUnreliableReady(new PeerId("same-host")) && clientTwo.IsUnreliableReady(new PeerId("same-host")) &&
            hostOne.IsUnreliableReady(new PeerId("alice")) && hostTwo.IsUnreliableReady(new PeerId("alice")));
        int first = 0, second = 0;
        hostOne.UnreliableReceived += (_, _) => Interlocked.Increment(ref first);
        hostTwo.UnreliableReceived += (_, _) => Interlocked.Increment(ref second);
        await clientOne.SendUnreliableAsync(new PeerId("same-host"), new byte[] { 1 });
        await Until(() => Volatile.Read(ref first) == 1);
        Assert.Equal(0, Volatile.Read(ref second));
        await clientTwo.SendUnreliableAsync(new PeerId("same-host"), new byte[] { 2 });
        await Until(() => Volatile.Read(ref second) == 1);
        Assert.Equal(1, Volatile.Read(ref first));
    }
    [Fact]
    public async Task RelayForwardsOnUdpOnlyAndPausingDoesNotBreakReliable()
    {
        int port = Port(); var options = new RelayConnectOptions { Port = port, RoomId = "room", Scope = "scope", HostPeerId = new PeerId("host"), HostCredential = "secret" };
        using var server = new TouchSocketRelayServer(new Authorizer());
        await server.StartAsync(new RelayListenOptions { Port = port });
        using var host = new RelayHostWire(); using var client = new RelayClientWire(new PeerId("host"));
        await host.ConnectAsync(options, _ => Task.FromResult((new PeerId("alice"), "ok")), _ => { });
        await host.ActivateAsync(); await client.ConnectAsync(options);
        Assert.Equal("ok", await client.RequestAdmissionAsync("join"));
        await Until(() => host.IsUnreliableReady(new PeerId("alice")) && client.IsUnreliableReady(new PeerId("host")));
        var message = Signal(); host.UnreliableReceived += (peer, bytes) => message.TrySetResult(peer.Value + ":" + Encoding.UTF8.GetString(bytes.Span));
        await client.SendUnreliableAsync(new PeerId("host"), Encoding.UTF8.GetBytes("pose"));
        Assert.Equal("alice:pose", await message.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var reply = Signal(); client.UnreliableReceived += (peer, bytes) => reply.TrySetResult(peer.Value + ":" + Encoding.UTF8.GetString(bytes.Span));
        await host.SendUnreliableAsync(new PeerId("alice"), Encoding.UTF8.GetBytes("downstream"));
        Assert.Equal("host:downstream", await reply.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => server.GetStatistics().UdpForwardedDatagrams > 0);
        server.UdpForwardingEnabled = false;
        await client.SendUnreliableAsync(new PeerId("host"), Encoding.UTF8.GetBytes("drop"));
        var reliable = Signal(); host.Received += (_, bytes) => reliable.TrySetResult(Encoding.UTF8.GetString(bytes));
        await client.SendAsync(new PeerId("host"), Encoding.UTF8.GetBytes("still connected"));
        Assert.Equal("still connected", await reliable.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => server.GetStatistics().UdpDroppedDatagrams > 0);
        server.UdpForwardingEnabled = true;
        await client.RebindDatagramsAsync();
        await host.RebindDatagramsAsync();
        await Until(() => host.IsUnreliableReady(new PeerId("alice")) && client.IsUnreliableReady(new PeerId("host")));
        host.Dispose();
        await Until(() => !client.IsConnected);
    }
}
