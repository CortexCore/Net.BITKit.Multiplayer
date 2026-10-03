using System.Net;
using System.Security.Cryptography.X509Certificates;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Dmtp.Rpc;
using TouchSocket.Rpc;
using TouchSocket.Sockets;

namespace BITKit.Multiplayer.Samples.Arena;

/// <summary>Separate transport facade: only explicit DMTP RPC methods are exported.</summary>
public sealed class ArenaLobbyServer : IAsyncDisposable
{
    private readonly TcpDmtpService _service = new();
    private readonly ArenaLobbyRpc _rpc;

    public ArenaLobbyServer(ArenaLobbyStore store) => _rpc = new ArenaLobbyRpc(store);

    public async Task StartAsync(int port, IPAddress? bindAddress = null, string? certificatePath = null, string? certificatePassword = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        bindAddress ??= IPAddress.Loopback;
        var config = new TouchSocketConfig()
            .ConfigureContainer(c => c.AddRpcStore(store => store.RegisterServer(_rpc)))
            .ConfigurePlugins(p => p.UseDmtpRpc())
            .SetDmtpOption(o => o.VerifyToken = ArenaProtocol.LobbyToken)
            .SetListenIPHosts(new IPHost(bindAddress + ":" + port));
        if (certificatePath is not null)
            config.SetServiceSslOption(o => o.Certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword));
        await _service.SetupAsync(config);
        await _service.StartAsync();
    }

    public ValueTask DisposeAsync() { _service.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class ArenaLobbyRpc : SingletonRpcServer
{
    private readonly ArenaLobbyStore _store;
    public ArenaLobbyRpc(ArenaLobbyStore store) => _store = store;

    [DmtpRpc(InvokeKey = "arena.v2.guest")]
    public Task<AuthSession> Guest(string displayName) => _store.GuestAsync(displayName);
    [DmtpRpc(InvokeKey = "arena.v2.register")]
    public Task<AuthSession> Register(string username, string password) => _store.RegisterAsync(username, password);
    [DmtpRpc(InvokeKey = "arena.v2.login")]
    public Task<AuthSession> Login(string username, string password) => _store.LoginAsync(username, password);
    [DmtpRpc(InvokeKey = "arena.v2.create")]
    public Task<RoomRegistration> CreateRoom(string sessionToken, string name, string address, int port) => _store.CreateRoomAsync(sessionToken, name, address, port);
    [DmtpRpc(InvokeKey = "arena.v2.list")]
    public Task<RoomInfo[]> ListRooms(string sessionToken) => _store.ListRoomsAsync(sessionToken);
    [DmtpRpc(InvokeKey = "arena.v2.join")]
    public Task<JoinTicket> JoinRoom(string sessionToken, string roomId) => _store.JoinRoomAsync(sessionToken, roomId);
    [DmtpRpc(InvokeKey = "arena.v2.redeem")]
    public Task<AdmissionIdentity> RedeemTicket(string roomId, string hostToken, string ticket) => _store.RedeemTicketAsync(roomId, hostToken, ticket);
    [DmtpRpc(InvokeKey = "arena.v2.heartbeat")]
    public Task<bool> HeartbeatRoom(string roomId, string hostToken) => _store.HeartbeatRoomAsync(roomId, hostToken);
    [DmtpRpc(InvokeKey = "arena.v2.close")]
    public Task<bool> CloseRoom(string roomId, string hostToken) => _store.CloseRoomAsync(roomId, hostToken);
    [DmtpRpc(InvokeKey = "arena.v2.relay.reserve")]
    public Task<RoomRegistration> ReserveRelayRoom(string sessionToken, string name, string relayAddress, int relayPort, bool useTls) => _store.ReserveRelayRoomAsync(sessionToken, name, relayAddress, relayPort, useTls);
    [DmtpRpc(InvokeKey = "arena.v2.relay.validate")]
    public Task<RoomInfo?> ValidateRoomHost(string roomId, string hostToken) => _store.ValidateRoomHostAsync(roomId, hostToken);
    [DmtpRpc(InvokeKey = "arena.v2.relay.publish")]
    public Task<bool> PublishRelayRoom(string roomId, string hostToken) => _store.PublishRelayRoomAsync(roomId, hostToken);
}
