using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    /// <summary>
    /// Application payload forwarding layered above one authenticated room wire.
    /// Channel zero is reserved for RpcRuntime's native frames; application channels
    /// are opaque to the network package and share the same Direct or Relay route.
    /// </summary>
    public interface IRoomApplicationTransport
    {
        int MaxReliablePayloadBytes { get; }
        int MaxUnreliablePayloadBytes { get; }
        event Action<PeerId, byte, ReadOnlyMemory<byte>>? ReliableReceived;
        event Action<PeerId, byte, ReadOnlyMemory<byte>>? ForwardedUnreliableReceived;
        Task SendReliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload);
        Task SendUnreliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload);
    }

    /// <summary>
    /// Multiplexes opaque application frames with native RPC frames without changing
    /// DirectWire or RelayWire. Dispose only removes subscriptions; the underlying wire
    /// remains owned by the caller that created it.
    /// </summary>
    public class MultiplexedRoomWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams, IRoomApplicationTransport
    {
        private const uint Magic = 0x4B544246; // F B T K, little-endian marker.
        private const byte Version = 1;
        private const int HeaderBytes = 10;
        private const int ReliableLimit = 1024 * 1024;

        private readonly IRoomWire _inner;
        private readonly IRoomMemoryWire? _memory;
        private readonly IRoomDatagrams? _datagrams;
        private int _disposed;

        public MultiplexedRoomWire(IRoomWire inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _memory = inner as IRoomMemoryWire;
            _datagrams = inner as IRoomDatagrams;
            if (_memory != null) _memory.MemoryReceived += OnReliable;
            else _inner.Received += OnReliableBytes;
            if (_datagrams != null) _datagrams.UnreliableReceived += OnUnreliable;
            _inner.PeerLeft += OnPeerLeft;
        }

        public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _inner.IsConnected;
        public int MaxReliablePayloadBytes => ReliableLimit - HeaderBytes;
        public int MaxUnreliablePayloadBytes => Math.Max(0, (_datagrams?.MaxUnreliablePayloadBytes ?? 0) - HeaderBytes);
        public bool UnreliableEnabled
        {
            get => _datagrams?.UnreliableEnabled ?? false;
            set { if (_datagrams != null) _datagrams.UnreliableEnabled = value; }
        }

        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ReliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ForwardedUnreliableReceived;

        public Task SendAsync(PeerId peer, byte[] data) => _inner.SendAsync(peer, data);

        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload) =>
            _memory != null ? _memory.SendMemoryAsync(peer, payload) : new ValueTask(_inner.SendAsync(peer, payload.ToArray()));

        public bool IsUnreliableReady(PeerId peer) => _datagrams?.IsUnreliableReady(peer) == true;
        public DatagramStatistics GetDatagramStatistics() => _datagrams?.GetDatagramStatistics() ?? new DatagramStatistics();
        public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) =>
            _datagrams?.RebindDatagramsAsync(cancellationToken) ??
            Task.FromException(new NotSupportedException("The room wire has no datagram lane."));
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) =>
            _datagrams?.SendUnreliableAsync(peer, payload) ??
            Task.FromException(new NotSupportedException("The room wire has no datagram lane."));

        public Task SendReliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) =>
            SendWrappedAsync(peer, channel, payload, false);

        public Task SendUnreliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) =>
            SendWrappedAsync(peer, channel, payload, true);

        private async Task SendWrappedAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload, bool unreliable)
        {
            if (!IsConnected) throw new RpcException(RpcError.Disconnected, "Forwarding wire is closed.");
            if (channel == 0) throw new ArgumentOutOfRangeException(nameof(channel), "Channel zero is reserved for RpcRuntime.");
            var limit = unreliable ? MaxUnreliablePayloadBytes : MaxReliablePayloadBytes;
            if (payload.Length > limit) throw new RpcException(RpcError.LimitExceeded, "Forwarded payload exceeds room wire limit.");
            var frame = Wrap(channel, payload.Span);
            if (unreliable) await SendUnreliableAsync(peer, frame).ConfigureAwait(false);
            else await SendMemoryAsync(peer, frame).ConfigureAwait(false);
        }

        private void OnReliable(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (IsForwarded(payload))
            {
                if (!TryUnwrap(payload, out var channel, out var body)) return;
                try { ReliableReceived?.Invoke(peer, channel, body); } catch { }
                return;
            }
            try
            {
                if (MemoryReceived != null) MemoryReceived?.Invoke(peer, payload);
                else Received?.Invoke(peer, payload.ToArray());
            }
            catch { }
        }

        private void OnReliableBytes(PeerId peer, byte[] payload) => OnReliable(peer, payload);

        private void OnUnreliable(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (IsForwarded(payload))
            {
                if (!TryUnwrap(payload, out var channel, out var body)) return;
                try { ForwardedUnreliableReceived?.Invoke(peer, channel, body); } catch { }
                return;
            }
            try { UnreliableReceived?.Invoke(peer, payload); } catch { }
        }

        private void OnPeerLeft(PeerId peer)
        {
            try { PeerLeft?.Invoke(peer); } catch { }
        }

        private static byte[] Wrap(byte channel, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[HeaderBytes + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), Magic);
            frame[4] = Version;
            frame[5] = channel;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6, 4), payload.Length);
            payload.CopyTo(frame.AsSpan(HeaderBytes));
            return frame;
        }

        private static bool IsForwarded(ReadOnlyMemory<byte> frame) => frame.Length >= 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Span.Slice(0, 4)) == Magic;

        private static bool TryUnwrap(ReadOnlyMemory<byte> memory, out byte channel, out ReadOnlyMemory<byte> payload)
        {
            var frame = memory.Span;
            channel = 0;
            payload = default;
            if (frame.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(frame[..4]) != Magic ||
                frame[4] != Version || frame[5] == 0) return false;
            var length = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(6, 4));
            if (length < 0 || length != frame.Length - HeaderBytes) return false;
            channel = frame[5];
            payload = memory.Slice(HeaderBytes, length);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_memory != null) _memory.MemoryReceived -= OnReliable;
            else _inner.Received -= OnReliableBytes;
            if (_datagrams != null) _datagrams.UnreliableReceived -= OnUnreliable;
            _inner.PeerLeft -= OnPeerLeft;
        }
    }
}
