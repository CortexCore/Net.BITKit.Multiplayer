using System;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    /// <summary>Authenticated datagram lane on a room wire; raw packet I/O is supplied by an ITransportFactory.</summary>
    public interface IRoomDatagrams
    {
        int MaxUnreliablePayloadBytes { get; }
        bool UnreliableEnabled { get; set; }
        bool IsUnreliableReady(PeerId peer);
        // Borrowed during the callback only; consumers decode/copy before returning.
        event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        // Caller retains memory until this task finishes. No retransmission or TCP fallback.
        Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload);
        DatagramStatistics GetDatagramStatistics();
        // Client/reverse Host rotates its endpoint through authenticated control-plane coordination.
        Task RebindDatagramsAsync(CancellationToken cancellationToken = default);
    }

    public sealed class DatagramStatistics
    {
        public long SentDatagrams { get; set; }
        public long ReceivedDatagrams { get; set; }
        public long SentPayloadBytes { get; set; }
        public long ReceivedPayloadBytes { get; set; }
        public long RejectedDatagrams { get; set; }
        public long DroppedDatagrams { get; set; }
        public int BoundPeers { get; set; }
        public int LargestDatagramBytes { get; set; }
    }
}
