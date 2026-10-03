using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer.TouchSocket;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class RoomTransportPortsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpBytesAndFragmentedDatagramsRoundTripThroughSingleAuthenticatedWire(bool relay)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostId = new PeerId("host"); var peer = new PeerId("client");
        var tcpEcho = new TcpListener(IPAddress.Loopback, 0); tcpEcho.Start();
        using var udpEcho = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = ((IPEndPoint)tcpEcho.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port;
        using var localTcp = new PortReservation(); using var localUdp = new PortReservation();
        int clientTcpPort = localTcp.Port, clientUdpPort = localUdp.Port;
        localTcp.Dispose(); localUdp.Dispose();
        using var relayServer = relay ? new TouchSocketRelayServer(new Authorizer()) : null;
        using var hostDirect = relay ? null : new TouchSocketHostWire();
        using var clientDirect = relay ? null : new TouchSocketClientWire(hostId);
        using var hostRelay = relay ? new RelayHostWire() : null;
        using var clientRelay = relay ? new RelayClientWire(hostId) : null;
        using var portProbe = new PortReservation(); int port = portProbe.Port; portProbe.Dispose();
        IRoomWire hostWire = (IRoomWire?)hostRelay ?? hostDirect!;
        IRoomWire clientWire = (IRoomWire?)clientRelay ?? clientDirect!;
        using var hostMux = new RoomTransportHub();
        using var clientMux = new RoomTransportHub();
        hostMux.Attach("primary", relay ? RoomTransportKind.Relay : RoomTransportKind.Direct, hostWire, true);
        clientMux.Attach("primary", relay ? RoomTransportKind.Relay : RoomTransportKind.Direct, clientWire, true);
        try
        {
            if (relay)
            {
                await relayServer!.StartAsync(new RelayListenOptions { Port = port }, deadline.Token);
                var options = new RelayConnectOptions { Port = port, RoomId = "room", Scope = "scope", HostPeerId = hostId, HostCredential = "credential" };
                await hostRelay!.ConnectAsync(options, ticket => ticket == "ticket"
                    ? Task.FromResult((peer, "accepted")) : throw new InvalidOperationException(), _ => { }, cancellationToken: deadline.Token);
                await hostRelay.ActivateAsync(deadline.Token);
                await clientRelay!.ConnectAsync(options, deadline.Token);
                Assert.Equal("accepted", await clientRelay.RequestAdmissionAsync("ticket", deadline.Token));
            }
            else
            {
                await hostDirect!.StartAsync(port, "bridge", admission: _ => Task.FromResult((peer, "accepted")), admitted: _ => { });
                await clientDirect!.ConnectAsync("127.0.0.1", port, "bridge");
                Assert.Equal("accepted", await clientDirect.RequestAdmissionAsync("ticket", deadline.Token));
            }
            await Until(() => ((IRoomDatagrams)clientWire).IsUnreliableReady(hostId) && ((IRoomDatagrams)hostWire).IsUnreliableReady(peer), deadline.Token);
            await hostMux.OpenLocalAsync(true, hostId, new Dictionary<byte, int> { [40] = tcpPort, [41] = udpPort }, deadline.Token);
            await clientMux.OpenLocalAsync(false, hostId, new Dictionary<byte, int> { [40] = clientTcpPort, [41] = clientUdpPort }, deadline.Token);
            var echo = Task.Run(async () =>
            {
                using var socket = await tcpEcho.AcceptTcpClientAsync(deadline.Token);
                var buffer = new byte[4096]; var stream = socket.GetStream();
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0) await stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            });
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, clientTcpPort, deadline.Token);
            var bytes = Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray();
            var stream = tcp.GetStream(); await stream.WriteAsync(bytes, deadline.Token);
            var received = new byte[bytes.Length]; await stream.ReadExactlyAsync(received, deadline.Token);
            Assert.Equal(bytes, received);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var datagram = bytes.AsSpan(0, 2500).ToArray();
            await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, clientUdpPort), deadline.Token);
            var uploaded = await udpEcho.ReceiveAsync(deadline.Token);
            Assert.Equal(clientMux.HostDatagramPorts[41], uploaded.RemoteEndPoint.Port);
            Assert.Equal(datagram, uploaded.Buffer);
            await udpEcho.SendAsync(uploaded.Buffer, uploaded.RemoteEndPoint, deadline.Token);
            Assert.Equal(datagram, (await udp.ReceiveAsync(deadline.Token)).Buffer);
            if (relay) Assert.True(relayServer!.GetStatistics().UdpForwardedDatagrams > 0);
        }
        finally { clientMux.Dispose(); hostMux.Dispose(); tcpEcho.Stop(); }
    }

    private static async Task Until(Func<bool> ready, CancellationToken token)
    { while (!ready()) await Task.Delay(10, token); }

    private sealed class Authorizer : IRelayRoomAuthorizer
    {
        public Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken token) =>
            Task.FromResult<RelayRoomIdentity?>(credential == "credential" ? new RelayRoomIdentity { Scope = "scope", HostPeerId = new PeerId("host") } : null);
        public Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken token) => Task.FromResult(true);
        public Task CloseRoomAsync(string roomId, string credential, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class PortReservation : IDisposable
    {
        private readonly TcpListener _tcp;
        private readonly UdpClient _udp;
        public int Port { get; }
        public PortReservation()
        {
            _tcp = new TcpListener(IPAddress.Loopback, 0); _tcp.Start(); Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        }
        public void Dispose() { _tcp.Stop(); _udp.Dispose(); }
    }
}
