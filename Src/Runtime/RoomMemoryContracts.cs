using System;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    /// <summary>
    /// Borrowed-memory companion to IRoomWire. RpcRuntime uses this event instead of
    /// the legacy byte[] event when implemented. A payload is valid only until the
    /// synchronous receive callback returns. Deferred work must retain an owned lease.
    /// </summary>
    public interface IRoomMemoryWire
    {
        event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        /// <summary>
        /// Borrows the supplied memory through completion. Consume the ValueTask once,
        /// even on its synchronous path. Completion/fault must not precede the last use
        /// of caller memory by queued or operating-system writes.
        /// </summary>
        ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload);
    }
}
