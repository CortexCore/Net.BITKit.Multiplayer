using System;
using System.Collections.Generic;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.Transport;

[assembly: InternalsVisibleTo("BITKit.Multiplayer.DatagramTests")]

namespace BITKit.Multiplayer.TouchSocket
{
    // Credentials travel only in the reliable control plane. HMAC authenticates packets; it does NOT encrypt them.
    internal sealed class UdpLane : IDisposable
    {
        internal const int Maximum = 1200, PayloadMaximum = 900;
        private const int Header = 27, Tag = 32;
        private const byte Hello = 1, Challenge = 2, Proof = 3, Confirm = 4, Heartbeat = 5, Data = 6;
        private readonly object _gate = new object();
        private readonly Dictionary<Guid, Grant> _grants = new Dictionary<Guid, Grant>();
        private readonly bool _listener;
        private readonly Action<PeerId, ReadOnlyMemory<byte>, string> _receive;
        private readonly Action<PeerId, ReadOnlyMemory<byte>, string>? _forward;
        private readonly ITransportFactory _factory;
        private ITransport? _udp;
        private Task _transportCompletion = Task.CompletedTask;
        private IPEndPoint? _remote;
        private Timer? _timer;
        private int _inflight, _enabled = 1, _closed;
        private long _sent, _received, _sentBytes, _receivedBytes, _rejected, _dropped;
        private long _poolRents, _poolReturns, _poolBytes, _peakPoolBytes;
        private int _poolOutstanding, _peakPoolOutstanding;
        private int _largest;
        // Internal accounting covers only buffers this lane rents and returns, NOT TouchSocket's
        // own receive/send buffers, HMAC allocations, or caller-owned RPC payload memory.
        internal long PoolRents => Interlocked.Read(ref _poolRents);
        internal long PoolReturns => Interlocked.Read(ref _poolReturns);
        internal long PoolOutstandingBytes => Interlocked.Read(ref _poolBytes);
        internal long PeakPoolOutstandingBytes => Interlocked.Read(ref _peakPoolBytes);
        internal int PoolOutstanding => Volatile.Read(ref _poolOutstanding);
        internal int PeakPoolOutstanding => Volatile.Read(ref _peakPoolOutstanding);
        internal int InflightDataSends => Volatile.Read(ref _inflight);
        // Internal test seam: a delayed/failing send verifies rentals are held through Task completion.
        // Normal production sends call the injected ITransport.SendAsync.
        internal Func<ITransport, IPEndPoint, ReadOnlyMemory<byte>, Task>? SendOverride { get; set; }
        internal Task TransportCompletion { get { lock (_gate) return _transportCompletion; } }
        internal byte[] RentBuffer(int length)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            Interlocked.Increment(ref _poolRents);
            int outstanding = Interlocked.Increment(ref _poolOutstanding);
            long bytes = Interlocked.Add(ref _poolBytes, buffer.Length);
            int peak; while ((peak = Volatile.Read(ref _peakPoolOutstanding)) < outstanding &&
                Interlocked.CompareExchange(ref _peakPoolOutstanding, outstanding, peak) != peak) { }
            long peakBytes; while ((peakBytes = Interlocked.Read(ref _peakPoolBytes)) < bytes &&
                Interlocked.CompareExchange(ref _peakPoolBytes, bytes, peakBytes) != peakBytes) { }
            return buffer;
        }
        internal void ReturnBuffer(byte[] buffer)
        {
            // ArrayPool returns may retain capacity for reuse; no application reference survives this call.
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Add(ref _poolBytes, -buffer.Length);
            Interlocked.Decrement(ref _poolOutstanding);
            Interlocked.Increment(ref _poolReturns);
        }
        internal sealed class Grant
        {
            internal PeerId Peer;
            internal byte[] Id = Array.Empty<byte>(), Key = Array.Empty<byte>();
            internal Guid CredentialId;
            // Only used/disposed under the owning lane's _gate; never held across socket I/O.
            internal HMACSHA256? Mac;
            internal IPEndPoint? Endpoint;
            internal IPEndPoint? Pending;
            internal byte[]? Challenge;
            internal long LastSeen;
            internal long Created = DateTime.UtcNow.Ticks;
            internal ulong Next, Highest, Seen;
            internal int ControlBusy;
            internal bool Ready;
            internal volatile bool Revoked;
            internal TaskCompletionSource<bool> Bound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal UdpLane(bool listener, Action<PeerId, ReadOnlyMemory<byte>, string> receive, Action<PeerId, ReadOnlyMemory<byte>, string>? forward = null, ITransportFactory? factory = null)
        { _listener = listener; _receive = receive; _forward = forward; _factory = factory ?? new UdpTransportFactory(); }
        internal bool Enabled { get => Volatile.Read(ref _enabled) != 0; set => Volatile.Write(ref _enabled, value ? 1 : 0); }
        internal static string NewCredential()
        {
            var bytes = new byte[48]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        internal async Task Start(int port, IPAddress address, IPEndPoint? remote = null)
        {
            var udp = _factory.Create() ?? throw new InvalidOperationException("Transport factory returned no endpoint");
            try
            {
                if ((udp.Capabilities & TransportCapabilities.Unreliable) == 0)
                    throw new NotSupportedException("UDP room lane requires Unreliable transport capability");
                udp.Received += OnTransportReceived;
                udp.Faulted += OnTransportFaulted;
                await udp.StartAsync(new TransportOptions { LocalEndPoint = new IPEndPoint(address, port),
                    MaxPacketBytes = Maximum, MaxConcurrentSends = 64 }).ConfigureAwait(false);
                lock (_gate) { if (_closed != 0) throw new ObjectDisposedException(nameof(UdpLane)); _udp = udp; _transportCompletion = udp.Completion; _remote = remote; }
                var timer = new Timer(_ => Tick(), null, 1000, 3000);
                lock (_gate) { if (_closed != 0) timer.Dispose(); else _timer = timer; }
            }
            catch
            {
                udp.Received -= OnTransportReceived; udp.Faulted -= OnTransportFaulted;
                udp.Dispose();
                try { await udp.StopAsync().ConfigureAwait(false); } catch { }
                throw;
            }
        }
        private void OnTransportReceived(EndPoint source, ReadOnlyMemory<byte> payload, RpcDelivery delivery)
        {
            if (delivery != RpcDelivery.Unreliable) { Interlocked.Increment(ref _rejected); return; }
            OnPacket(source, payload); // borrowed only during this synchronous callback
        }
        private void OnTransportFaulted(Exception _) => Dispose(); // no reliable/TCP disconnect or fallback
        internal static IPEndPoint Resolve(string host, int port)
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                ip = Array.Find(Dns.GetHostAddresses(host), x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? throw new ArgumentException("IPv4 UDP address required", nameof(host));
            }
            return new IPEndPoint(ip, port);
        }
        internal Grant Install(PeerId peer, string credential)
        {
            var bytes = Convert.FromBase64String(credential);
            if (bytes.Length != 48) throw new ArgumentException("Invalid UDP credential", nameof(credential));
            var grant = new Grant { Peer = peer, Id = new byte[16], Key = new byte[32] };
            Buffer.BlockCopy(bytes, 0, grant.Id, 0, 16); Buffer.BlockCopy(bytes, 16, grant.Key, 0, 32);
            grant.CredentialId = new Guid(grant.Id);
            lock (_gate)
            {
                if (_closed != 0) throw new ObjectDisposedException(nameof(UdpLane));
                grant.Mac = new HMACSHA256(grant.Key);
                try
                {
                    foreach (var old in new List<Grant>(_grants.Values))
                        if (old.Peer.Equals(peer)) Revoke(old);
                    _grants.Add(grant.CredentialId, grant);
                }
                catch { grant.Mac.Dispose(); grant.Mac = null; throw; }
            }
            if (!_listener) _ = SendControl(grant, Hello, ReadOnlyMemory<byte>.Empty, _remote);
            return grant;
        }
        internal void Remove(PeerId peer)
        {
            lock (_gate) foreach (var grant in new List<Grant>(_grants.Values))
                if (grant.Peer.Equals(peer)) Revoke(grant);
        }
        internal bool Ready(PeerId peer)
        {
            lock (_gate) { foreach (var grant in _grants.Values) if (grant.Peer.Equals(peer) && grant.Ready &&
                DateTime.UtcNow.Ticks - grant.LastSeen < TimeSpan.FromSeconds(30).Ticks) return true; return false; }
        }
        internal Task Wait(Grant grant, CancellationToken token) => WaitFor(grant.Bound.Task, token);
        internal static async Task<T> WaitFor<T>(Task<T> task, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (timeout.Token.Register(() => canceled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
                    { token.ThrowIfCancellationRequested(); throw new TimeoutException("UDP binding timed out"); }
                    return await task.ConfigureAwait(false);
                }
            }
        }
        private static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }
        private static bool Same(IPEndPoint? a, IPEndPoint? b) => a != null && b != null && a.Equals(b);
        private static bool Fresh(Grant g, ulong nonce)
        {
            if (nonce == 0) return false;
            if (nonce > g.Highest) { var shift = nonce - g.Highest; g.Seen = (shift >= 64 ? 0 : g.Seen << (int)shift) | 1; g.Highest = nonce; return true; }
            var age = g.Highest - nonce;
            if (age >= 64 || (g.Seen & (1UL << (int)age)) != 0) return false;
            g.Seen |= 1UL << (int)age; return true;
        }
        private void OnPacket(EndPoint source, ReadOnlyMemory<byte> memory)
        {
            if (!(source is IPEndPoint endpoint) || memory.Length < Header + Tag || memory.Length > Maximum)
            { Interlocked.Increment(ref _rejected); return; }
            var span = memory.Span;
            if (span[25] != 0 || span[26] != 0) { Interlocked.Increment(ref _rejected); return; }
            Grant g; byte kind = span[0], response = 0;
            byte[]? responseBody = null;
            PeerId sender = default;
            ReadOnlyMemory<byte> payload = default;
            string claim = "";
            bool deliver = false;
            lock (_gate)
            {
                if (!_grants.TryGetValue(new Guid(span.Slice(1, 16)), out g!)) { Interlocked.Increment(ref _rejected); return; }
                Span<byte> digest = stackalloc byte[Tag];
                if (!g.Mac!.TryComputeHash(span.Slice(0, memory.Length - Tag), digest, out int written) || written != Tag ||
                    !CryptographicOperations.FixedTimeEquals(digest, span.Slice(memory.Length - Tag)))
                { Interlocked.Increment(ref _rejected); return; }
                if (!_listener && !Same(endpoint, _remote)) { Interlocked.Increment(ref _rejected); return; }
                var body = memory.Slice(Header, memory.Length - Header - Tag);
                long now = DateTime.UtcNow.Ticks;
                if (now - (g.Ready ? g.LastSeen : g.Created) >= TimeSpan.FromSeconds(30).Ticks)
                {
                    Revoke(g); Interlocked.Increment(ref _rejected); return;
                }
                // Validate type, shape and endpoint BEFORE spending a nonce. Malformed or wrong-endpoint
                // frames must not advance the replay window and deny subsequent valid packets.
                if (kind == Hello)
                {
                    if (!_listener || body.Length != 0 || g.Ready && !Same(endpoint, g.Endpoint)) { Interlocked.Increment(ref _rejected); return; }
                }
                else if (kind == Proof)
                {
                    if (!_listener || body.Length != 16 || !Same(endpoint, g.Pending) || g.Challenge == null || !Equal(body.Span, g.Challenge))
                    { Interlocked.Increment(ref _rejected); return; }
                }
                else if (kind == Challenge)
                {
                    if (_listener || body.Length != 16 || g.Ready) { Interlocked.Increment(ref _rejected); return; }
                }
                else if (kind == Confirm)
                {
                    if (_listener || body.Length != 0) { Interlocked.Increment(ref _rejected); return; }
                }
                else
                {
                    if (!g.Ready || _listener && !Same(endpoint, g.Endpoint)) { Interlocked.Increment(ref _rejected); return; }
                    if (kind == Heartbeat)
                    { if (body.Length != 0) { Interlocked.Increment(ref _rejected); return; } }
                    else if (kind == Data)
                    {
                        if (body.Length < 1 || body.Length > PayloadMaximum + 129 || body.Span[0] > 128 || body.Span[0] + 1 > body.Length ||
                            body.Length - body.Span[0] - 1 > PayloadMaximum) { Interlocked.Increment(ref _rejected); return; }
                    }
                    else { Interlocked.Increment(ref _rejected); return; }
                }
                ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(17, 8));
                if (!Fresh(g, nonce)) { Interlocked.Increment(ref _rejected); return; }
                if (kind == Hello)
                {
                    g.Pending = endpoint; g.Challenge = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(g.Challenge);
                    response = Challenge; responseBody = g.Challenge;
                }
                else if (kind == Challenge) { response = Proof; responseBody = body.ToArray(); }
                else if (kind == Proof)
                {
                    g.Endpoint = endpoint; g.Pending = null; g.Challenge = null; g.Ready = true;
                    g.LastSeen = now; g.Bound.TrySetResult(true); response = Confirm;
                }
                else if (kind == Confirm)
                { g.Ready = true; g.LastSeen = now; g.Bound.TrySetResult(true); }
                else if (kind == Heartbeat) g.LastSeen = now;
                else
                {
                    if (!Enabled) { Interlocked.Increment(ref _dropped); return; }
                    int peerLength = body.Span[0]; claim = System.Text.Encoding.UTF8.GetString(body.Span.Slice(1, peerLength));
                    payload = body.Slice(1 + peerLength); sender = g.Peer;
                    g.LastSeen = now;
                    Interlocked.Increment(ref _received); Interlocked.Add(ref _receivedBytes, payload.Length);
                    RecordLargest(memory.Length); deliver = true;
                }
            }
            // Socket sends and application callbacks can acquire other owners' locks. Neither runs under _gate.
            if (response != 0) _ = SendControl(g, response, responseBody ?? Array.Empty<byte>(), endpoint);
            // A packet committed before revocation may already be executing; never dispatch a
            // queued callback after observing a revoked grant. Owners re-check live room routing.
            if (deliver && !g.Revoked) try { if (_forward != null) _forward(sender, payload, claim); else _receive(sender, payload, claim); }
                catch { Interlocked.Increment(ref _dropped); }
        }
        private void Revoke(Grant g) // caller holds _gate
        {
            if (g.Revoked) return;
            _grants.Remove(g.CredentialId); g.Revoked = true; g.Ready = false;
            g.Mac?.Dispose(); g.Mac = null;
            g.Bound.TrySetCanceled();
        }
        internal async Task Send(PeerId peer, ReadOnlyMemory<byte> payload, string claim = "")
        {
            int nameLength = System.Text.Encoding.UTF8.GetByteCount(claim);
            if (payload.Length > PayloadMaximum || payload.Length + Header + Tag + 1 + nameLength > Maximum)
                throw new RpcException(RpcError.LimitExceeded, "UDP payload exceeds limit");
            if (nameLength > 128) throw new ArgumentException("UDP target identity too long", nameof(claim));
            if (!Enabled) { Interlocked.Increment(ref _dropped); return; }
            Grant? grant = null; IPEndPoint? endpoint = null;
            lock (_gate) foreach (var g in _grants.Values) if (g.Peer.Equals(peer) && g.Ready && DateTime.UtcNow.Ticks - g.LastSeen < TimeSpan.FromSeconds(30).Ticks)
            { grant = g; endpoint = _listener ? g.Endpoint : _remote; break; }
            if (grant == null || endpoint == null) throw new RpcException(RpcError.Disconnected, "UDP binding unavailable");
            if (Interlocked.Increment(ref _inflight) > 64) { Interlocked.Decrement(ref _inflight); Interlocked.Increment(ref _dropped); return; }
            try
            {
                int length = 1 + nameLength + payload.Length;
                var body = RentBuffer(length);
                try
                {
                    body[0] = (byte)nameLength;
                    System.Text.Encoding.UTF8.GetBytes(claim.AsSpan(), body.AsSpan(1, nameLength));
                    payload.CopyTo(body.AsMemory(1 + nameLength, payload.Length));
                    await Transmit(grant, Data, body.AsMemory(0, length), endpoint).ConfigureAwait(false);
                }
                finally { ReturnBuffer(body); }
                Interlocked.Increment(ref _sent); Interlocked.Add(ref _sentBytes, payload.Length);
            }
            finally { Interlocked.Decrement(ref _inflight); }
        }
        private Task SendControl(Grant g, byte type, ReadOnlyMemory<byte> body, IPEndPoint? endpoint)
        {
            if (endpoint == null) return Task.CompletedTask;
            // Keep handshake responses possible while a Hello/heartbeat send is still in flight.
            // Limit per-grant concurrent control sends, including an authenticated Hello flood.
            if (Interlocked.Increment(ref g.ControlBusy) <= 4) return SafeControl(g, type, body, endpoint);
            Interlocked.Decrement(ref g.ControlBusy);
            return Task.CompletedTask;
        }
        private async Task SafeControl(Grant g, byte type, ReadOnlyMemory<byte> body, IPEndPoint endpoint)
        { try { await Transmit(g, type, body, endpoint).ConfigureAwait(false); } catch { } finally { Interlocked.Decrement(ref g.ControlBusy); } }
        private async Task Transmit(Grant g, byte type, ReadOnlyMemory<byte> body, IPEndPoint endpoint)
        {
            int length = Header + body.Length + Tag;
            var wire = RentBuffer(length);
            try
            {
                ITransport udp;
                lock (_gate)
                {
                    if (_closed != 0 || g.Revoked || !_grants.TryGetValue(g.CredentialId, out var current) || !ReferenceEquals(current, g))
                        throw new RpcException(RpcError.Disconnected, "UDP grant revoked");
                    udp = _udp ?? throw new RpcException(RpcError.Disconnected, "UDP socket closed");
                    if (g.Next == ulong.MaxValue)
                    { Revoke(g); throw new RpcException(RpcError.Disconnected, "UDP nonce exhausted; rotate credentials"); }
                    wire[0] = type;
                    Buffer.BlockCopy(g.Id, 0, wire, 1, 16);
                    BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(17, 8), ++g.Next);
                    wire[25] = wire[26] = 0; // version/flags reserved; always authenticated.
                    body.CopyTo(wire.AsMemory(Header, body.Length));
                    if (!g.Mac!.TryComputeHash(wire.AsSpan(0, length - Tag), wire.AsSpan(length - Tag, Tag), out int written) || written != Tag)
                        throw new CryptographicException("UDP authentication tag could not be written");
                }
                // Revoke may dispose the MAC after signing; I/O only owns the independent frame.
                var overrideSend = SendOverride;
                if (overrideSend == null) await udp.SendAsync(endpoint, wire.AsMemory(0, length), RpcDelivery.Unreliable, CancellationToken.None).ConfigureAwait(false);
                else await overrideSend(udp, endpoint, wire.AsMemory(0, length)).ConfigureAwait(false);
                RecordLargest(length);
            }
            finally { ReturnBuffer(wire); }
        }
        private void RecordLargest(int length)
        { int current; while ((current = Volatile.Read(ref _largest)) < length && Interlocked.CompareExchange(ref _largest, length, current) != current) { } }
        private void Tick()
        {
            var controls = new List<(Grant Grant, byte Type, byte[]? Body, IPEndPoint? Endpoint)>();
            lock (_gate)
            {
                if (_closed != 0) return;
                long now = DateTime.UtcNow.Ticks;
                foreach (var g in new List<Grant>(_grants.Values))
                {
                    if (now - (g.Ready ? g.LastSeen : g.Created) >= TimeSpan.FromSeconds(30).Ticks) { Revoke(g); continue; }
                    if (_listener && g.Pending != null && g.Challenge != null)
                        controls.Add((g, Challenge, g.Challenge, g.Pending));
                    else if (g.Ready) controls.Add((g, Heartbeat, null, _listener ? g.Endpoint : _remote));
                    else if (!_listener) controls.Add((g, Hello, null, _remote));
                }
            }
            foreach (var item in controls) _ = SendControl(item.Grant, item.Type, item.Body ?? Array.Empty<byte>(), item.Endpoint);
        }
        internal DatagramStatistics Statistics()
        {
            int bound = 0; ITransport? transport;
            lock (_gate)
            {
                foreach (var g in _grants.Values) if (g.Ready && DateTime.UtcNow.Ticks - g.LastSeen < TimeSpan.FromSeconds(30).Ticks) bound++;
                transport = _udp;
            }
            // The endpoint itself rejects packets > MaxPacketBytes before calling OnPacket.
            // Keep those rejections visible without counting MAC/replay failures twice.
            long transportRejected = transport?.GetStatistics().RejectedPackets ?? 0;
            return new DatagramStatistics { SentDatagrams = Interlocked.Read(ref _sent), ReceivedDatagrams = Interlocked.Read(ref _received),
                SentPayloadBytes = Interlocked.Read(ref _sentBytes), ReceivedPayloadBytes = Interlocked.Read(ref _receivedBytes),
                RejectedDatagrams = Interlocked.Read(ref _rejected) + transportRejected, DroppedDatagrams = Interlocked.Read(ref _dropped), BoundPeers = bound, LargestDatagramBytes = Volatile.Read(ref _largest) };
        }
        public void Dispose()
        {
            ITransport? socket; Timer? timer;
            lock (_gate)
            {
                if (_closed != 0) return;
                _closed = 1;
                foreach (var g in new List<Grant>(_grants.Values)) Revoke(g);
                socket = _udp; _udp = null; timer = _timer; _timer = null;
            }
            timer?.Dispose();
            if (socket != null)
            {
                socket.Received -= OnTransportReceived; socket.Faulted -= OnTransportFaulted;
                socket.Dispose(); // nonblocking; Completion/StopAsync signal full native shutdown
            }
        }
    }
}
