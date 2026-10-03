using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    public enum RoomTransportKind : byte { Direct, Relay, Replay, Bot }

    public sealed class RoomTransportBinding : IDisposable
    {
        private RoomTransportHub? _owner;
        internal readonly string Name;
        internal RoomTransportBinding(RoomTransportHub owner, string name) { _owner = owner; Name = name; }
        public void Bind(PeerId peer) => _owner?.BindPeer(Name, peer);
        public void Unbind(PeerId peer) => _owner?.UnbindPeer(Name, peer);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Detach(Name);
    }

    /// <summary>
    /// One room-facing transport surface over multiple transport implementations.
    /// Direct, Relay, Replay and Bot endpoints are interchangeable and may coexist.
    /// Room identity and peer route are transport metadata; game services never branch on transport kind.
    /// </summary>
    public sealed class RoomTransportHub : IRoomWire, IRoomMemoryWire, IRoomDatagrams, IRoomApplicationTransport
    {
        private sealed class Entry
        {
            internal string Name = string.Empty;
            internal RoomTransportKind Kind;
            internal IRoomWire Wire = null!;
            internal IRoomMemoryWire? Memory;
            internal IRoomDatagrams? Datagrams;
            internal bool Default;
            internal readonly HashSet<PeerId> Peers = new HashSet<PeerId>();
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<PeerId, Entry> _routes = new();
        private int _disposed;
        private RoomPorts? _ports;
        public event Action? Closed;
        public int LocalPort { get; private set; }
        public IReadOnlyDictionary<byte, int> HostDatagramPorts => _ports?.HostDatagramPorts ?? new Dictionary<byte, int>();

        public bool IsConnected => Volatile.Read(ref _disposed) == 0 && Entries().Any(e => e.Wire.IsConnected);
        public int MaxReliablePayloadBytes => 1024 * 1024 - 10;
        public int MaxUnreliablePayloadBytes => Math.Max(0, Entries().Where(e => e.Datagrams != null).Select(e => e.Datagrams!.MaxUnreliablePayloadBytes).DefaultIfEmpty(0).Max() - 10);
        public bool UnreliableEnabled
        {
            get => Entries().Where(e => e.Datagrams != null).All(e => e.Datagrams!.UnreliableEnabled);
            set { foreach (var entry in Entries()) if (entry.Datagrams != null) entry.Datagrams.UnreliableEnabled = value; }
        }
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ReliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ForwardedUnreliableReceived;

        public RoomTransportBinding Attach(string name, RoomTransportKind kind, IRoomWire wire, bool defaultRoute = false)
        {
            if (string.IsNullOrWhiteSpace(name) || wire == null) throw new ArgumentException("Transport name and wire are required.");
            lock (_gate)
            {
                if (_disposed != 0 || _entries.ContainsKey(name)) throw new InvalidOperationException("Transport is already attached.");
                var entry = new Entry { Name = name, Kind = kind, Wire = wire, Memory = wire as IRoomMemoryWire, Datagrams = wire as IRoomDatagrams, Default = defaultRoute };
                entry.Wire.Received += (peer, data) => Received?.Invoke(peer, data);
                entry.Wire.PeerLeft += peer => { UnbindPeer(name, peer); PeerLeft?.Invoke(peer); };
                if (entry.Memory != null) entry.Memory.MemoryReceived += (peer, data) => ReceiveReliable(peer, data);
                if (entry.Datagrams != null) entry.Datagrams.UnreliableReceived += (peer, data) => ReceiveUnreliable(peer, data);
                if (entry.Wire is IRoomApplicationTransport application)
                {
                    application.ReliableReceived += (peer, channel, data) => ReliableReceived?.Invoke(peer, channel, data);
                    application.ForwardedUnreliableReceived += (peer, channel, data) => ForwardedUnreliableReceived?.Invoke(peer, channel, data);
                }
                _entries.Add(name, entry);
                return new RoomTransportBinding(this, name);
            }
        }

        public async Task OpenLocalAsync(bool host, PeerId hostPeer, IReadOnlyDictionary<byte, int> services,
            CancellationToken cancellationToken = default)
        {
            if (host)
            {
                _ports = new RoomPorts(this, this, true, hostPeer, services);
                _ports.Closed += () => Closed?.Invoke();
                return;
            }
            for (int attempt = 0; attempt < 16; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                if (port > 65532) continue;
                try
                {
                    var ports = new Dictionary<byte, int>(services);
                    var local = new RoomPorts(this, this, false, hostPeer, ports);
                    await local.ListenAsync(cancellationToken).ConfigureAwait(false);
                    _ports = local; LocalPort = ports.TryGetValue(40, out var assigned) ? assigned : port;
                    _ports.Closed += () => Closed?.Invoke();
                    return;
                }
                catch (SocketException) { }
            }
            throw new InvalidOperationException("No free local room transport ports.");
        }

        public void BindPeer(string transportName, PeerId peer)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(transportName, out var entry)) throw new InvalidOperationException("Transport is not attached.");
                if (_routes.TryGetValue(peer, out var previous)) previous.Peers.Remove(peer);
                _routes[peer] = entry; entry.Peers.Add(peer);
            }
        }

        public void UnbindPeer(string transportName, PeerId peer)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(transportName, out var entry)) entry.Peers.Remove(peer);
                if (_routes.TryGetValue(peer, out var current) && current.Name == transportName) _routes.Remove(peer);
            }
        }

        public Task SendAsync(PeerId peer, byte[] data) => Route(peer).Wire.SendAsync(peer, data);
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            var entry = Route(peer);
            return entry.Memory != null ? entry.Memory.SendMemoryAsync(peer, payload) : new ValueTask(entry.Wire.SendAsync(peer, payload.ToArray()));
        }
        public bool IsUnreliableReady(PeerId peer) => Route(peer).Datagrams?.IsUnreliableReady(peer) == true;
        public DatagramStatistics GetDatagramStatistics() => Route(peer: null).Datagrams?.GetDatagramStatistics() ?? new DatagramStatistics();
        public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) => Route(null).Datagrams?.RebindDatagramsAsync(cancellationToken) ?? Task.CompletedTask;
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) => Route(peer).Datagrams?.SendUnreliableAsync(peer, payload) ?? Task.FromException(new RpcException(RpcError.Disconnected, "No datagram transport is bound."));
        public Task SendReliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) => SendApplication(peer, channel, payload, false);
        public Task SendUnreliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) => SendApplication(peer, channel, payload, true);

        private Task SendApplication(PeerId peer, byte channel, ReadOnlyMemory<byte> payload, bool unreliable)
        {
            if (channel == 0) throw new ArgumentOutOfRangeException(nameof(channel), "Channel zero is reserved for RpcRuntime.");
            var entry = Route(peer);
            if (entry.Wire is IRoomApplicationTransport app)
                return unreliable ? app.SendUnreliableAsync(peer, channel, payload) : app.SendReliableAsync(peer, channel, payload);
            var frame = Wrap(channel, payload.Span);
            if (unreliable) return entry.Datagrams?.SendUnreliableAsync(peer, frame) ?? Task.FromException(new RpcException(RpcError.Disconnected, "No datagram transport is bound."));
            return entry.Memory != null ? entry.Memory.SendMemoryAsync(peer, frame).AsTask() : entry.Wire.SendAsync(peer, frame);
        }

        private void ReceiveReliable(PeerId peer, ReadOnlyMemory<byte> data)
        { if (TryUnwrap(data, out var channel, out var payload)) ReliableReceived?.Invoke(peer, channel, payload); else MemoryReceived?.Invoke(peer, data); }
        private void ReceiveUnreliable(PeerId peer, ReadOnlyMemory<byte> data)
        { if (TryUnwrap(data, out var channel, out var payload)) ForwardedUnreliableReceived?.Invoke(peer, channel, payload); else UnreliableReceived?.Invoke(peer, data); }
        private static byte[] Wrap(byte channel, ReadOnlySpan<byte> payload)
        {
            var data = new byte[payload.Length + 10]; BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x4B544246);
            data[4] = 1; data[5] = channel; BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(6, 4), payload.Length); payload.CopyTo(data.AsSpan(10)); return data;
        }
        private static bool TryUnwrap(ReadOnlyMemory<byte> memory, out byte channel, out ReadOnlyMemory<byte> payload)
        {
            var span = memory.Span; channel = 0; payload = default;
            if (span.Length < 10 || BinaryPrimitives.ReadUInt32LittleEndian(span[..4]) != 0x4B544246 || span[4] != 1 || span[5] == 0) return false;
            var length = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(6, 4));
            if (length < 0 || length != span.Length - 10) return false;
            channel = span[5]; payload = memory.Slice(10, length); return true;
        }

        private Entry Route(PeerId? peer)
        {
            lock (_gate)
            {
                if (_disposed != 0) throw new RpcException(RpcError.Disconnected, "Transport hub is closed.");
                if (peer.HasValue && _routes.TryGetValue(peer.Value, out var route)) return route;
                var entry = _entries.Values.FirstOrDefault(e => e.Default && e.Wire.IsConnected) ?? _entries.Values.FirstOrDefault(e => e.Wire.IsConnected);
                return entry ?? throw new RpcException(RpcError.Disconnected, "No connected transport is attached.");
            }
        }

        private Entry[] Entries() { lock (_gate) return _entries.Values.ToArray(); }
        internal void Detach(string name) { lock (_gate) { if (_entries.Remove(name, out var entry)) foreach (var peer in entry.Peers) _routes.Remove(peer); } }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _ports?.Dispose(); _ports = null;
            Entry[] entries; lock (_gate) { entries = _entries.Values.ToArray(); _entries.Clear(); _routes.Clear(); }
            foreach (var entry in entries) entry.Wire.Dispose();
        }
    }

    /// <summary>Virtual transport for replay injection and bot peers.</summary>
    public sealed class VirtualRoomTransport : IRoomWire, IRoomMemoryWire, IRoomDatagrams, IRoomApplicationTransport
    {
        public bool IsConnected { get; private set; } = true;
        public int MaxReliablePayloadBytes => 1024 * 1024 - 10;
        public int MaxUnreliablePayloadBytes => 900;
        public bool UnreliableEnabled { get; set; } = true;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ReliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? ForwardedUnreliableReceived;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? SentReliable;
        public event Action<PeerId, byte, ReadOnlyMemory<byte>>? SentUnreliable;
        public Task SendAsync(PeerId peer, byte[] data) { SentReliable?.Invoke(peer, 0, data); return Task.CompletedTask; }
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload) { MemoryReceived?.Invoke(peer, payload); return new ValueTask(); }
        public bool IsUnreliableReady(PeerId peer) => IsConnected && UnreliableEnabled;
        public DatagramStatistics GetDatagramStatistics() => new DatagramStatistics();
        public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) { SentUnreliable?.Invoke(peer, 0, payload); return Task.CompletedTask; }
        public Task SendReliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) { SentReliable?.Invoke(peer, channel, payload); return Task.CompletedTask; }
        public Task SendUnreliableAsync(PeerId peer, byte channel, ReadOnlyMemory<byte> payload) { SentUnreliable?.Invoke(peer, channel, payload); return Task.CompletedTask; }
        public void InjectReliable(PeerId peer, byte channel, ReadOnlyMemory<byte> payload)
        { if (channel == 0) MemoryReceived?.Invoke(peer, payload); else ReliableReceived?.Invoke(peer, channel, payload); }
        public void InjectUnreliable(PeerId peer, byte channel, ReadOnlyMemory<byte> payload)
        { if (channel == 0) UnreliableReceived?.Invoke(peer, payload); else ForwardedUnreliableReceived?.Invoke(peer, channel, payload); }
        public void RemovePeer(PeerId peer) => PeerLeft?.Invoke(peer);
        public void Dispose() { IsConnected = false; }
    }
}
