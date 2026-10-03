using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    [Flags]
    public enum TransportCapabilities { None = 0, Unreliable = 1, ReliableOrdered = 2 }

    /// <summary>One owned packet endpoint. Not an authenticated room or an account connection.</summary>
    public interface ITransport : IDisposable
    {
        TransportCapabilities Capabilities { get; }
        EndPoint? LocalEndPoint { get; }
        /// <summary>Completes after stop/failure and after all operations release borrowed buffers.</summary>
        Task Completion { get; }
        /// <summary>Payload is borrowed until callback return. No callback may retain it without copying.</summary>
        event Action<EndPoint, ReadOnlyMemory<byte>, RpcDelivery>? Received;
        event Action<Exception>? Faulted;
        /// <summary>Single-use instance. Bind before admitting traffic; cancellation applies to startup.</summary>
        Task StartAsync(TransportOptions options, CancellationToken cancellationToken = default);
        /// <summary>
        /// Reject unsupported delivery and oversize messages. Retain caller memory until completion;
        /// cancellation never releases memory still referenced by an OS operation. No silent fallback.
        /// </summary>
        ValueTask SendAsync(EndPoint destination, ReadOnlyMemory<byte> payload,
            RpcDelivery delivery = RpcDelivery.Unreliable, CancellationToken cancellationToken = default);
        TransportStatistics GetStatistics();
        /// <summary>Signal stop and await Completion. Dispose signals stop without blocking a receive callback.</summary>
        Task StopAsync();
    }

    /// <summary>DI replaces this factory; every Create returns a fresh endpoint owned/disposed by the room lane.</summary>
    public interface ITransportFactory
    {
        string Name { get; }
        ITransport Create();
    }

    public sealed class TransportOptions
    {
        public IPEndPoint LocalEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 0);
        public int MaxPacketBytes { get; set; } = 1200;
        public int MaxConcurrentSends { get; set; } = 128;
    }

    public sealed class TransportStatistics
    {
        public long SentPackets { get; set; }
        public long ReceivedPackets { get; set; }
        public long RejectedPackets { get; set; }
        public int InFlightSends { get; set; }
        public long BufferRents { get; set; }
        public long BufferReturns { get; set; }
        public long OutstandingBufferBytes { get; set; }
    }
}
