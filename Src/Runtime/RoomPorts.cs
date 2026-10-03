using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
    /// <summary>Loopback-only endpoints forwarded over an authenticated room wire.
    /// No remote address or port is accepted from the wire; the Host supplies the service allowlist.
    /// Stream bytes use reliable delivery; datagrams use only the unreliable lane.</summary>
    internal sealed class RoomPorts : IDisposable
    {
        private const byte Open = 1, Data = 2, Close = 3, DatagramOpen = 4, DatagramAck = 5;
        private readonly IRoomWire _wire;
        private readonly IRoomApplicationTransport _forwarding;
        private readonly bool _host;
        private readonly PeerId _hostPeer;
        private readonly Dictionary<byte, int> _services;
        private readonly object _gate = new object();
        private readonly Dictionary<(PeerId, byte), TcpClient> _streams = new Dictionary<(PeerId, byte), TcpClient>();
        private readonly Dictionary<(PeerId, byte), UdpClient> _udp = new Dictionary<(PeerId, byte), UdpClient>();
        private readonly Dictionary<byte, IPEndPoint> _localUdpPeers = new Dictionary<byte, IPEndPoint>();
        private readonly Dictionary<byte, TaskCompletionSource<int>> _udpReady = new Dictionary<byte, TaskCompletionSource<int>>();
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly Queue<(PeerId Peer, byte Channel, byte[] Data)> _queue = new Queue<(PeerId, byte, byte[])>();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private int _queueBytes, _disposed, _udpInFlight;
        private int _datagramSequence;
        private readonly Dictionary<(PeerId, byte, int), Fragments> _fragments = new Dictionary<(PeerId, byte, int), Fragments>();
        private sealed class Fragments
        {
            internal readonly byte[][] Parts;
            internal readonly int Length;
            internal readonly DateTime Expires = DateTime.UtcNow.AddSeconds(1);
            internal int Received;
            internal Fragments(int count, int length) { Parts = new byte[count][]; Length = length; }
        }
        public event Action? Closed;
        public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _wire.IsConnected;
        public Dictionary<byte, int> HostDatagramPorts { get; } = new Dictionary<byte, int>();

        public RoomPorts(IRoomWire wire, IRoomApplicationTransport forwarding, bool host,
            PeerId hostPeer, IReadOnlyDictionary<byte, int> services)
        {
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
            _host = host; _hostPeer = hostPeer;
            _services = services.ToDictionary(p => p.Key, p => p.Value);
            if (_services.Count > 16 || _services.Any(p => p.Key == 0 || p.Value < 1 || p.Value > 65535))
                throw new ArgumentException("Invalid loopback service allowlist.");
            _forwarding.ReliableReceived += Receive;
            _forwarding.ForwardedUnreliableReceived += ReceiveDatagram;
            _wire.PeerLeft += PeerLeft;
            _ = Consume();
        }

        /// <summary>Client listen ports: even channels are TCP, odd channels are UDP.
        /// Channel allocation is application-owned and identical at both ends.</summary>
        public async Task ListenAsync(CancellationToken token = default)
        {
            if (_host) throw new InvalidOperationException("Only the Client creates local listeners.");
            try
            {
                foreach (var pair in _services)
                {
                    if ((pair.Key & 1) == 0)
                    {
                        var listener = new TcpListener(IPAddress.Loopback, pair.Value);
                        listener.Start(1); _listeners.Add(listener);
                        _ = Accept(listener, pair.Key);
                    }
                    else
                    {
                        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, pair.Value));
                        lock (_gate) _udp.Add((_hostPeer, pair.Key), socket);
                        var reply = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _udpReady.Add(pair.Key, reply);
                        await _forwarding.SendReliableAsync(_hostPeer, pair.Key, new byte[] { DatagramOpen }).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        using (timeout.Token.Register(() => reply.TrySetCanceled()))
                            HostDatagramPorts.Add(pair.Key, await reply.Task.ConfigureAwait(false));
                        _ = ReadDatagrams(_hostPeer, pair.Key, socket);
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        private async Task Accept(TcpListener listener, byte channel)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    lock (_gate)
                    {
                        if (_disposed != 0 || _streams.ContainsKey((_hostPeer, channel))) { client.Dispose(); continue; }
                        _streams.Add((_hostPeer, channel), client);
                    }
                    await _forwarding.SendReliableAsync(_hostPeer, channel, new byte[] { Open }).ConfigureAwait(false);
                    _ = ReadStream(_hostPeer, channel, client);
                }
            }
            catch { if (!_stop.IsCancellationRequested) Dispose(); }
        }

        private void Receive(PeerId peer, byte channel, ReadOnlyMemory<byte> data)
        {
            if (!_services.ContainsKey(channel) || !_host && !peer.Equals(_hostPeer)) return;
            lock (_gate)
            {
                if (_disposed != 0) return;
                if (data.Length < 1 || data.Length > 32769 || _queue.Count >= 256 || data.Length > 4 * 1024 * 1024 - _queueBytes)
                { Dispose(); return; }
                _queue.Enqueue((peer, channel, data.ToArray())); _queueBytes += data.Length;
            }
            _wake.Release();
        }

        private async Task Consume()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                    (PeerId Peer, byte Channel, byte[] Data) message;
                    lock (_gate) { message = _queue.Dequeue(); _queueBytes -= message.Data.Length; }
                    var key = (message.Peer, message.Channel);
                    var op = message.Data[0];
                    if (_host && op == Open && (message.Channel & 1) == 0 && message.Data.Length == 1)
                    {
                        lock (_gate) if (_streams.ContainsKey(key) || _streams.Count >= 128) throw new IOException("Duplicate or excess tunnel stream.");
                        var client = new TcpClient { NoDelay = true };
                        lock (_gate) _streams.Add(key, client);
                        var connect = client.ConnectAsync(IPAddress.Loopback, _services[message.Channel]);
                        if (await Task.WhenAny(connect, Task.Delay(5000, _stop.Token)).ConfigureAwait(false) != connect)
                            throw new TimeoutException("Loopback service did not connect.");
                        await connect.ConfigureAwait(false);
                        _ = ReadStream(message.Peer, message.Channel, client);
                    }
                    else if (_host && op == DatagramOpen && (message.Channel & 1) == 1 && message.Data.Length == 1)
                    {
                        UdpClient socket;
                        lock (_gate)
                        {
                            if (!_udp.TryGetValue(key, out socket!))
                            {
                                if (_udp.Count >= 128) throw new IOException("Too many tunnel datagram peers.");
                                socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                                socket.Connect(IPAddress.Loopback, _services[message.Channel]); _udp.Add(key, socket);
                                _ = ReadDatagrams(message.Peer, message.Channel, socket);
                            }
                        }
                        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
                        await _forwarding.SendReliableAsync(message.Peer, message.Channel,
                            new byte[] { DatagramAck, (byte)port, (byte)(port >> 8) }).ConfigureAwait(false);
                    }
                    else if (!_host && op == DatagramAck && message.Data.Length == 3 && _udpReady.TryGetValue(message.Channel, out var reply))
                        reply.TrySetResult(message.Data[1] | message.Data[2] << 8);
                    else if (op == Data && (message.Channel & 1) == 0)
                    {
                        TcpClient client;
                        lock (_gate) if (!_streams.TryGetValue(key, out client!)) continue;
                        await client.GetStream().WriteAsync(message.Data, 1, message.Data.Length - 1, _stop.Token).ConfigureAwait(false);
                    }
                    else if (op == Close && message.Data.Length == 1)
                    {
                        lock (_gate) if (_streams.Remove(key, out var client)) client.Dispose();
                    }
                    else throw new IOException("Invalid tunnel control.");
                }
            }
            catch { if (!_stop.IsCancellationRequested) Dispose(); }
        }

        private async Task ReadStream(PeerId peer, byte channel, TcpClient client)
        {
            try
            {
                var buffer = new byte[32769]; buffer[0] = Data;
                var stream = client.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    var count = await stream.ReadAsync(buffer, 1, buffer.Length - 1, _stop.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    await _forwarding.SendReliableAsync(peer, channel, buffer.AsMemory(0, count + 1)).ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                bool removed;
                lock (_gate) removed = _streams.TryGetValue((peer, channel), out var current) && ReferenceEquals(current, client) && _streams.Remove((peer, channel));
                client.Dispose();
                if (removed && !_stop.IsCancellationRequested)
                    try { await _forwarding.SendReliableAsync(peer, channel, new byte[] { Close }).ConfigureAwait(false); } catch { }
            }
        }

        private async Task ReadDatagrams(PeerId peer, byte channel, UdpClient socket)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync().ConfigureAwait(false);
                    if (!_host)
                    {
                        if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;
                        lock (_gate)
                        {
                            if (_localUdpPeers.TryGetValue(channel, out var existing) && !existing.Equals(packet.RemoteEndPoint)) continue;
                            _localUdpPeers[channel] = packet.RemoteEndPoint;
                        }
                    }
                    if (packet.Buffer.Length > 16384) continue;
                    var capacity = _forwarding.MaxUnreliablePayloadBytes - 12;
                    if (capacity < 512) continue;
                    var count = Math.Max(1, (packet.Buffer.Length + capacity - 1) / capacity);
                    var sequence = Interlocked.Increment(ref _datagramSequence);
                    for (int index = 0; index < count; index++)
                    {
                        var size = Math.Min(capacity, packet.Buffer.Length - index * capacity);
                        var frame = new byte[size + 12];
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), sequence);
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), packet.Buffer.Length);
                        frame[8] = (byte)index; frame[9] = (byte)count;
                        Array.Copy(packet.Buffer, index * capacity, frame, 12, size);
                        await _forwarding.SendUnreliableAsync(peer, channel, frame).ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }

        private void ReceiveDatagram(PeerId peer, byte channel, ReadOnlyMemory<byte> payload)
        {
            if (!_services.ContainsKey(channel) || (channel & 1) == 0 || !_host && !peer.Equals(_hostPeer)) return;
            UdpClient socket; IPEndPoint? target = null;
            lock (_gate)
            {
                if (_disposed != 0 || !_udp.TryGetValue((peer, channel), out socket!)) return;
                if (!_host && !_localUdpPeers.TryGetValue(channel, out target)) return;
                if (payload.Length < 12) return;
                var span = payload.Span;
                int sequence = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4));
                int length = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
                int index = span[8], count = span[9];
                if (length < 0 || length > 16384 || count < 1 || count > 32 || index >= count || span[10] != 0 || span[11] != 0) return;
                var capacity = _forwarding.MaxUnreliablePayloadBytes - 12;
                if (count != Math.Max(1, (length + capacity - 1) / capacity) ||
                    payload.Length - 12 != Math.Min(capacity, length - index * capacity)) return;
                foreach (var expired in _fragments.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) _fragments.Remove(expired);
                var key = (peer, channel, sequence);
                if (!_fragments.TryGetValue(key, out var fragments))
                {
                    if (_fragments.Count >= 64) return;
                    _fragments.Add(key, fragments = new Fragments(count, length));
                }
                if (fragments.Length != length || fragments.Parts.Length != count || fragments.Parts[index] != null) return;
                fragments.Parts[index] = payload.Slice(12).ToArray();
                if (++fragments.Received != count) return;
                var joined = new byte[length];
                for (int i = 0; i < count; i++) Array.Copy(fragments.Parts[i], 0, joined, i * capacity, fragments.Parts[i].Length);
                _fragments.Remove(key);
                payload = joined;
            }
            if (Interlocked.Increment(ref _udpInFlight) > 64) { Interlocked.Decrement(ref _udpInFlight); return; }
            _ = DeliverDatagram(socket, target, payload.ToArray());
        }

        private async Task DeliverDatagram(UdpClient socket, IPEndPoint? target, byte[] bytes)
        {
            try
            {
                if (_host) await socket.SendAsync(bytes, bytes.Length).ConfigureAwait(false);
                else await socket.SendAsync(bytes, bytes.Length, target!).ConfigureAwait(false);
            }
            catch { }
            finally { Interlocked.Decrement(ref _udpInFlight); }
        }

        private void PeerLeft(PeerId peer)
        {
            if (!_host) { if (peer.Equals(_hostPeer)) Dispose(); return; }
            lock (_gate)
            {
                foreach (var key in _streams.Keys.Where(k => k.Item1.Equals(peer)).ToArray()) { _streams[key].Dispose(); _streams.Remove(key); }
                foreach (var key in _udp.Keys.Where(k => k.Item1.Equals(peer)).ToArray()) { _udp[key].Dispose(); _udp.Remove(key); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _forwarding.ReliableReceived -= Receive; _forwarding.ForwardedUnreliableReceived -= ReceiveDatagram; _wire.PeerLeft -= PeerLeft;
            lock (_gate)
            {
                foreach (var listener in _listeners) listener.Stop();
                foreach (var stream in _streams.Values) stream.Dispose();
                foreach (var socket in _udp.Values) socket.Dispose();
                foreach (var reply in _udpReady.Values) reply.TrySetCanceled();
                _streams.Clear(); _udp.Clear(); _queue.Clear(); _queueBytes = 0;
            }
            try { Closed?.Invoke(); } catch { }
        }
    }
}
