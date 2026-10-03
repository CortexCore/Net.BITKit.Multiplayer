using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.TouchSocket
{
    /// <summary>Application admission; Relay never interprets gameplay or executes RPC bodies.</summary>
    public interface IRelayRoomAuthorizer
    {
        Task<RelayRoomIdentity?> AuthorizeHostAsync(string roomId, string credential, CancellationToken cancellationToken);
        Task<bool> ActivateRoomAsync(string roomId, string credential, CancellationToken cancellationToken);
        Task CloseRoomAsync(string roomId, string credential, CancellationToken cancellationToken);
    }
    public sealed class RelayRoomIdentity
    {
        public string Scope { get; set; } = "";
        public PeerId HostPeerId { get; set; }
    }
    public sealed class RelayListenOptions
    {
        // Development loopback may use plain TCP. Public deployments MUST configure CertificatePath
        // and clients MUST use verified TLS: UDP HMAC authenticates packets, but does not encrypt them.
        public int Port { get; set; } = 17892;
        public IPAddress BindAddress { get; set; } = IPAddress.Loopback;
        public string VerifyToken { get; set; } = "bitkit-relay-v1";
        public int MaxRooms { get; set; } = 128;
        public int MaxClientsPerRoom { get; set; } = 64;
        public int MaxMessageBytes { get; set; } = 1024 * 1024;
        public int MaxQueuedMessagesPerConnection { get; set; } = 128;
        public int MaxQueuedBytesPerConnection { get; set; } = 4 * 1024 * 1024;
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
    }
    public sealed class RelayConnectOptions
    {
        // UseTls and a validated TlsTargetHost are required when credentials cross an untrusted network.
        public string Address { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 17892;
        public string VerifyToken { get; set; } = "bitkit-relay-v1";
        public string RoomId { get; set; } = "";
        public string Scope { get; set; } = "";
        public PeerId HostPeerId { get; set; }
        public string HostCredential { get; set; } = "";
        public bool UseTls { get; set; }
        public string? TlsTargetHost { get; set; }
    }
    public sealed class RelayStatistics
    {
        public int Rooms { get; set; }
        public int Clients { get; set; }
        public int PendingAdmissions { get; set; }
        public long ForwardedMessages { get; set; }
        public long ForwardedBytes { get; set; }
        public long RejectedMessages { get; set; }
        public long UdpForwardedDatagrams { get; set; }
        public long UdpForwardedBytes { get; set; }
        public long UdpRejectedDatagrams { get; set; }
        public long UdpDroppedDatagrams { get; set; }
        public int UdpBoundPeers { get; set; }
        public int LargestUdpDatagramBytes { get; set; }
        public bool UdpForwardingEnabled { get; set; } = true;
    }
}
