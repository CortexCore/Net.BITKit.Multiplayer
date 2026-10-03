using System.Net.Security;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Dmtp.Rpc;
using TouchSocket.Rpc;
using TouchSocket.Sockets;

namespace BITKit.Multiplayer.Samples.Arena;

public sealed class ArenaLobbyClient : IArenaRelayLobbyApi
{
    private readonly TcpDmtpClient _socket = new();
    private bool _disposed;
    private ArenaLobbyClient() { }

    public static async Task<ArenaLobbyClient> ConnectAsync(string address, int port, CancellationToken token = default)
        => await ConnectAsync(address, port, false, null, token);

    public static async Task<ArenaLobbyClient> ConnectAsync(string address, int port, bool useTls, string? tlsTargetHost = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || port is < 1 or > 65535) throw new ArgumentException("Invalid lobby endpoint");
        if (useTls && string.IsNullOrWhiteSpace(tlsTargetHost ?? address)) throw new ArgumentException("Invalid TLS target host");
        var client = new ArenaLobbyClient();
        try
        {
            var config = new TouchSocketConfig()
                .ConfigurePlugins(p => p.UseDmtpRpc())
                .SetDmtpOption(o => o.VerifyToken = ArenaProtocol.LobbyToken)
                .SetRemoteIPHost(address + ":" + port);
            // TouchSocket 4.3.9's SslOption default callback ACCEPTS ALL certificates.
            // Explicitly require platform chain and target-host validation to succeed.
            if (useTls) config.SetClientSslOption(o =>
            {
                o.TargetHost = tlsTargetHost ?? address;
                o.CertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None;
            });
            await client._socket.SetupAsync(config);
            await client._socket.ConnectAsync().WaitAsync(token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    private async Task<T> Call<T>(string key, params object[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (T)(await _socket.GetDmtpRpcActor().InvokeAsync(key, typeof(T), new DmtpInvokeOption(5000) { SerializationType = SerializationType.Json }, args))!;
    }

    public Task<AuthSession> GuestAsync(string displayName) => Call<AuthSession>("arena.v2.guest", displayName);
    public Task<AuthSession> RegisterAsync(string username, string password) => Call<AuthSession>("arena.v2.register", username, password);
    public Task<AuthSession> LoginAsync(string username, string password) => Call<AuthSession>("arena.v2.login", username, password);
    public Task<RoomRegistration> CreateRoomAsync(string sessionToken, string name, string address, int port) => Call<RoomRegistration>("arena.v2.create", sessionToken, name, address, port);
    public Task<RoomInfo[]> ListRoomsAsync(string sessionToken) => Call<RoomInfo[]>("arena.v2.list", sessionToken);
    public Task<JoinTicket> JoinRoomAsync(string sessionToken, string roomId) => Call<JoinTicket>("arena.v2.join", sessionToken, roomId);
    public Task<AdmissionIdentity> RedeemTicketAsync(string roomId, string hostToken, string ticket) => Call<AdmissionIdentity>("arena.v2.redeem", roomId, hostToken, ticket);
    public Task<bool> HeartbeatRoomAsync(string roomId, string hostToken) => Call<bool>("arena.v2.heartbeat", roomId, hostToken);
    public Task<bool> CloseRoomAsync(string roomId, string hostToken) => Call<bool>("arena.v2.close", roomId, hostToken);
    public Task<RoomRegistration> ReserveRelayRoomAsync(string sessionToken, string name, string relayAddress, int relayPort, bool useTls) => Call<RoomRegistration>("arena.v2.relay.reserve", sessionToken, name, relayAddress, relayPort, useTls);
    public Task<RoomInfo?> ValidateRoomHostAsync(string roomId, string hostToken) => Call<RoomInfo?>("arena.v2.relay.validate", roomId, hostToken);
    public Task<bool> PublishRelayRoomAsync(string roomId, string hostToken) => Call<bool>("arena.v2.relay.publish", roomId, hostToken);
    public ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; _socket.Dispose(); } return ValueTask.CompletedTask; }
}
