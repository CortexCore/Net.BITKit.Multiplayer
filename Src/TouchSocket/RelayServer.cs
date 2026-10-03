using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.Transport;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Sockets;

namespace BITKit.Multiplayer.TouchSocket
{
    public sealed class TouchSocketRelayServer : IDisposable
    {
        private sealed class Room
        {
            internal string Id = "", Scope = "", Credential = "";
            internal PeerId HostId;
            internal Connection Host = null!;
            internal bool Active;
            internal Task<bool>? ActivationTask;
            internal readonly Dictionary<PeerId, Connection> Peers = new Dictionary<PeerId, Connection>();
            internal readonly HashSet<PeerId> Used = new HashSet<PeerId>();
        }
        private sealed class Connection
        {
            internal RelaySocket Socket = null!;
            internal Room? Room;
            internal PeerId? Peer;
            internal bool IsHost, Pending, Leaving;
            internal long Request;
            internal int Released;
            internal PeerId UdpIdentity;
            internal TaskCompletionSource<bool>? AdmissionDelivered;
        }
        private sealed class Pending
        {
            internal Room Room = null!;
            internal Connection Client = null!;
            internal TaskCompletionSource<(PeerId Peer, string Response)?> Done = new TaskCompletionSource<(PeerId, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly IRelayRoomAuthorizer _authorizer;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        private readonly HashSet<string> _registering = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _closingRooms = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        private readonly HashSet<Connection> _connections = new HashSet<Connection>();
        private readonly TcpDmtpService _service = new TcpDmtpService();
        private readonly UdpLane _udp;
        private readonly Dictionary<string, Connection> _sessions = new Dictionary<string, Connection>();
        private readonly Dictionary<PeerId, Connection> _udpRouting = new Dictionary<PeerId, Connection>();
        private RelayListenOptions? _options;
        private bool _started;
        private int _disposed, _inflight;
        private long _nextRequest, _messages, _bytes, _rejected;
        private long _udpRejected, _udpDropped, _udpForwarded, _udpBytes;
        private int _udpForwardInFlight;
        internal int UdpForwardInFlight => Volatile.Read(ref _udpForwardInFlight);
        private int _udpForwarding = 1;
        public bool UdpForwardingEnabled { get => Volatile.Read(ref _udpForwarding) != 0; set => Volatile.Write(ref _udpForwarding, value ? 1 : 0); }
        public event Action<string>? Diagnostic;
        public TouchSocketRelayServer(IRelayRoomAuthorizer authorizer, ITransportFactory? transportFactory = null)
        { _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer)); _udp = new UdpLane(true, (_, __, ___) => { }, ForwardUdp, transportFactory); }
        public async Task StartAsync(RelayListenOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null || options.Port < 1 || options.Port > 65535 || options.BindAddress == null ||
                string.IsNullOrWhiteSpace(options.VerifyToken) || options.MaxRooms < 1 || options.MaxClientsPerRoom < 1 ||
                options.MaxMessageBytes < 4096 || options.MaxMessageBytes > RelayProtocol.AbsoluteLimit ||
                options.MaxQueuedMessagesPerConnection < 1 || options.MaxQueuedBytesPerConnection < options.MaxMessageBytes + 5)
                throw new ArgumentException("Invalid relay listener limits", nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) { if (_started || _disposed != 0) throw new InvalidOperationException("Relay already started or disposed"); _started = true; _options = options; }
            var config = new TouchSocketConfig()
                .ConfigurePlugins(plugins =>
                {
                    plugins.AddDmtpReceivedPlugin((IDmtpActorObject actor, DmtpMessageEventArgs e) =>
                    {
                        if (actor is ITcpDmtpSessionClient session && e.DmtpMessage.ProtocolFlags == RelayProtocol.Protocol)
                        {
                            if (e.DmtpMessage.Memory.Length < 1 || e.DmtpMessage.Memory.Length > options.MaxMessageBytes + 1)
                            { Interlocked.Increment(ref _rejected); session.Dispose(); }
                            else OnFrame(session, e.DmtpMessage.Memory);
                        }
                        return Task.CompletedTask;
                    });
                    plugins.AddDmtpClosedPlugin((IDmtpActorObject actor) =>
                    { if (actor is ITcpDmtpSessionClient session) OnClosed(session.Id); });
                    plugins.AddTcpClosedPlugin((ITcpSession actor) =>
                    { if (actor is ITcpDmtpSessionClient session) OnClosed(session.Id); });
                })
                .SetDmtpOption(o => o.VerifyToken = options.VerifyToken)
                .SetListenIPHosts(new IPHost(options.BindAddress + ":" + options.Port));
            if (options.CertificatePath != null)
                config.SetServiceSslOption(o => o.Certificate = new X509Certificate2(options.CertificatePath, options.CertificatePassword));
            try { await _udp.Start(options.Port, options.BindAddress); await _service.SetupAsync(config).ConfigureAwait(false); await _service.StartAsync().ConfigureAwait(false); }
            catch { _udp.Dispose(); _service.Dispose(); lock (_gate) { _started = false; _disposed = 1; } throw; }
        }
        public RelayStatistics GetStatistics()
        {
            var udp = _udp.Statistics();
            lock (_gate) return new RelayStatistics {
                Rooms = _rooms.Count, Clients = _rooms.Values.Sum(r => r.Peers.Count), PendingAdmissions = _pending.Count,
                ForwardedMessages = Interlocked.Read(ref _messages), ForwardedBytes = Interlocked.Read(ref _bytes),
                RejectedMessages = Interlocked.Read(ref _rejected),
                UdpForwardedDatagrams = Interlocked.Read(ref _udpForwarded), UdpForwardedBytes = Interlocked.Read(ref _udpBytes),
                UdpRejectedDatagrams = Interlocked.Read(ref _udpRejected) + udp.RejectedDatagrams,
                UdpDroppedDatagrams = Interlocked.Read(ref _udpDropped) + udp.DroppedDatagrams,
                UdpBoundPeers = udp.BoundPeers, LargestUdpDatagramBytes = udp.LargestDatagramBytes,
                UdpForwardingEnabled = UdpForwardingEnabled
            };
        }
        private void ForwardUdp(PeerId sender, ReadOnlyMemory<byte> borrowed, string targetName)
        {
            PeerId destination, claimedSender;
            lock (_gate)
            {
                if (!_udpRouting.TryGetValue(sender, out var source) || source.Released != 0 || source.Room == null ||
                    !source.Room.Active || source.Leaving) { Interlocked.Increment(ref _udpRejected); return; }
                var room = source.Room;
                if (source.IsHost)
                {
                    if (!room.Peers.TryGetValue(new PeerId(targetName), out var client) || client.Leaving || client.Released != 0)
                    { Interlocked.Increment(ref _udpRejected); return; }
                    destination = client.UdpIdentity; claimedSender = room.HostId;
                }
                else
                {
                    if (targetName.Length != 0) { Interlocked.Increment(ref _udpRejected); return; }
                    if (!source.Peer.HasValue || !room.Peers.TryGetValue(source.Peer.Value, out var member) || member != source)
                    { Interlocked.Increment(ref _udpRejected); return; }
                    destination = room.Host.UdpIdentity; claimedSender = source.Peer.Value;
                }
            }
            if (!UdpForwardingEnabled) { Interlocked.Increment(ref _udpDropped); return; }
            // Reserve capacity BEFORE copying borrowed TouchSocket receive memory. Each accepted
            // forwarding task owns a pool rental until its UDP send completes or faults.
            if (Interlocked.Increment(ref _udpForwardInFlight) > 64)
            { Interlocked.Decrement(ref _udpForwardInFlight); Interlocked.Increment(ref _udpDropped); return; }
            byte[] owned;
            try { owned = _udp.RentBuffer(borrowed.Length); }
            catch { Interlocked.Decrement(ref _udpForwardInFlight); Interlocked.Increment(ref _udpDropped); return; }
            try { borrowed.CopyTo(owned.AsMemory(0, borrowed.Length)); }
            catch { _udp.ReturnBuffer(owned); Interlocked.Decrement(ref _udpForwardInFlight); Interlocked.Increment(ref _udpDropped); return; }
            _ = ForwardOwned(destination, claimedSender, owned, borrowed.Length);
        }
        private async Task ForwardOwned(PeerId destination, PeerId sender, byte[] bytes, int length)
        {
            try { await _udp.Send(destination, bytes.AsMemory(0, length), sender.Value).ConfigureAwait(false); Interlocked.Increment(ref _udpForwarded); Interlocked.Add(ref _udpBytes, length); }
            catch { Interlocked.Increment(ref _udpDropped); }
            finally { _udp.ReturnBuffer(bytes); Interlocked.Decrement(ref _udpForwardInFlight); }
        }
        private async Task GrantUdp(Connection c)
        {
            PeerId peer;
            var credential = UdpLane.NewCredential();
            lock (_gate)
            {
                if (c.Released != 0 || c.Room == null || !c.Room.Active && !c.IsHost) return;
                if (!c.IsHost && !c.Peer.HasValue) return;
                if (string.IsNullOrWhiteSpace(c.UdpIdentity.Value))
                {
                    c.UdpIdentity = new PeerId("udp-" + Guid.NewGuid().ToString("N"));
                    _udpRouting.Add(c.UdpIdentity, c);
                }
                peer = c.UdpIdentity;
            }
            _udp.Install(peer, credential);
            bool valid;
            lock (_gate)
            {
                valid = c.Released == 0 && c.Room != null && (c.IsHost || c.Peer.HasValue && !c.Leaving) &&
                    _udpRouting.TryGetValue(peer, out var current) && current == c;
            }
            if (!valid) { _udp.Remove(peer); return; }
            try { await c.Socket.Send(RelayOp.UdpGrant, RelayProtocol.Pack(w => w.Write(credential))).ConfigureAwait(false); }
            catch { _udp.Remove(peer); }
        }
        private void OnFrame(ITcpDmtpSessionClient session, ReadOnlyMemory<byte> frame)
        {
            Connection? connection;
            bool launch = false;
            lock (_gate)
            {
                if (_disposed != 0) return;
                if (!_sessions.TryGetValue(session.Id, out connection))
                {
                    var opts = _options!;
                    if (_inflight >= opts.MaxRooms * (opts.MaxClientsPerRoom + 1) + 64)
                    { try { session.Dispose(); } catch { } return; }
                    var socket = new RelaySocket(bytes => session.DmtpActor.SendAsync(RelayProtocol.Protocol, bytes),
                        () => session.Dispose(), opts.MaxMessageBytes, opts.MaxQueuedMessagesPerConnection, opts.MaxQueuedBytesPerConnection);
                    connection = new Connection { Socket = socket };
                    _connections.Add(connection); _sessions.Add(session.Id, connection); _inflight++; launch = true;
                }
            }
            connection!.Socket.Enqueue(frame);
            if (launch) _ = HandleConnection(connection, session.Id); // bounded by the configured connection limit
        }
        private void OnClosed(string id)
        {
            Connection? connection;
            lock (_gate) _sessions.TryGetValue(id, out connection);
            if (connection != null)
            {
                connection.Socket.Dispose();
                _ = Disconnect(connection); // bounded by live sessions; removes routing before external CloseRoom
            }
        }
        private async Task HandleConnection(Connection connection, string sessionId)
        {
            string phase = "handshake";
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                {
                    using var first = await connection.Socket.Read(timeout.Token).ConfigureAwait(false);
                    if (first.Op == RelayOp.Register) { phase = "register"; await Register(connection, first.Payload, timeout.Token).ConfigureAwait(false); }
                    else if (first.Op == RelayOp.Select) { phase = "select"; await Select(connection, first.Payload).ConfigureAwait(false); }
                    else throw new InvalidDataException("Expected handshake");
                }
                phase = "session";
                while (Volatile.Read(ref _disposed) == 0)
                {
                    using var frame = await connection.Socket.Read(CancellationToken.None).ConfigureAwait(false);
                    if (connection.IsHost) await HostFrame(connection, frame.Op, frame.Payload).ConfigureAwait(false);
                    else await ClientFrame(connection, frame.Op, frame.Payload).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Interlocked.Increment(ref _rejected); try { Diagnostic?.Invoke("Relay " + phase + " rejected: " + ex.GetType().Name); } catch { } }
            finally
            {
                await Disconnect(connection).ConfigureAwait(false);
                lock (_gate) { _sessions.Remove(sessionId); _inflight--; }
            }
        }
        private async Task Register(Connection c, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var input = RelayProtocol.Parse(bytes, r => (Token: RelayProtocol.Text(r, 256), Room: RelayProtocol.Text(r, 256), Scope: RelayProtocol.Text(r, 256), Host: RelayProtocol.Text(r, 128), Credential: RelayProtocol.Text(r)));
            if (input.Token != _options!.VerifyToken || string.IsNullOrWhiteSpace(input.Room) || string.IsNullOrWhiteSpace(input.Scope) || string.IsNullOrWhiteSpace(input.Host) || string.IsNullOrWhiteSpace(input.Credential)) throw new InvalidDataException("Invalid host registration");
            lock (_gate)
            {
                // Do not invoke external authorization while an old owner is still closing:
                // its CloseRoomAsync could otherwise delete the replacement's reservation.
                if (_rooms.ContainsKey(input.Room) || _closingRooms.Contains(input.Room) || _registering.Contains(input.Room) ||
                    _rooms.Count + _registering.Count + _closingRooms.Count >= _options.MaxRooms)
                    throw new InvalidDataException("Room unavailable");
                _registering.Add(input.Room);
            }
            try
            {
                var identity = await _authorizer.AuthorizeHostAsync(input.Room, input.Credential, token).ConfigureAwait(false);
                if (identity == null || identity.Scope != input.Scope || identity.HostPeerId.Value != input.Host) throw new InvalidDataException("Host authorization denied");
                lock (_gate)
                {
                    if (_disposed != 0 || c.Released != 0 || _rooms.ContainsKey(input.Room) || _closingRooms.Contains(input.Room))
                        throw new InvalidDataException("Room unavailable");
                    var room = new Room { Id = input.Room, Scope = input.Scope, Credential = input.Credential, HostId = identity.HostPeerId, Host = c };
                    _rooms.Add(room.Id, room); c.Room = room; c.IsHost = true;
                }
                await c.Socket.Send(RelayOp.Ack, RelayProtocol.Pack(w => w.Write(true))).ConfigureAwait(false);
                await GrantUdp(c).ConfigureAwait(false);
            }
            finally { lock (_gate) _registering.Remove(input.Room); }
        }
        private async Task Select(Connection c, ReadOnlyMemory<byte> bytes)
        {
            var input = RelayProtocol.Parse(bytes, r => (Token: RelayProtocol.Text(r, 256), Room: RelayProtocol.Text(r, 256), Scope: RelayProtocol.Text(r, 256), Host: RelayProtocol.Text(r, 128)));
            lock (_gate)
            {
                if (input.Token != _options!.VerifyToken || !_rooms.TryGetValue(input.Room, out var room) || !room.Active || room.Scope != input.Scope || room.HostId.Value != input.Host ||
                    _connections.Count(x => x.Room == room && !x.IsHost) >= _options.MaxClientsPerRoom)
                    throw new InvalidDataException("Room unavailable");
                if (c.Released != 0) throw new InvalidDataException("Session closed");
                c.Room = room;
            }
            await c.Socket.Send(RelayOp.Ack, RelayProtocol.Pack(w => w.Write(true))).ConfigureAwait(false);
        }
        private async Task HostFrame(Connection c, RelayOp op, ReadOnlyMemory<byte> bytes)
        {
            var room = c.Room!;
            if (op == RelayOp.Activate && bytes.Length == 0)
            {
                bool valid;
                lock (_gate) valid = !room.Active && _rooms.TryGetValue(room.Id, out var found) && found == room;
                if (!valid) throw new InvalidDataException("Room already active");
                var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) room.ActivationTask = activation.Task;
                bool approved;
                try { approved = await _authorizer.ActivateRoomAsync(room.Id, room.Credential, CancellationToken.None).ConfigureAwait(false); activation.TrySetResult(approved); }
                catch { activation.TrySetResult(false); throw; }
                lock (_gate) { valid = approved && _disposed == 0 && _rooms.TryGetValue(room.Id, out var found) && found == room; if (valid) room.Active = true; }
                await c.Socket.Send(RelayOp.Ack, RelayProtocol.Pack(w => w.Write(valid))).ConfigureAwait(false);
                if (!valid) throw new InvalidDataException("Activation denied");
            }
            else if (op == RelayOp.AdmissionResult)
            {
                var result = RelayProtocol.Parse(bytes, r => (Id: r.ReadInt64(), Peer: RelayProtocol.Text(r, 128), Response: RelayProtocol.Text(r)));
                Pending? pending;
                bool success = false;
                lock (_gate)
                {
                    if (_pending.TryGetValue(result.Id, out pending) && pending.Room == room)
                    {
                        _pending.Remove(result.Id);
                        var client = pending.Client;
                        if (!string.IsNullOrWhiteSpace(result.Peer) && client.Room == room && client.Pending && !client.Leaving && !room.Used.Contains(new PeerId(result.Peer)) && result.Peer != room.HostId.Value && room.Peers.Count < _options!.MaxClientsPerRoom)
                        {
                            var peer = new PeerId(result.Peer);
                            client.Peer = peer; room.Peers.Add(peer, client); room.Used.Add(peer); success = true;
                        }
                    }
                }
                // Host must release a redeemed ticket when a result arrived after timeout/disconnect.
                await c.Socket.Send(RelayOp.AdmissionReply, RelayProtocol.Pack(w => { w.Write(result.Id); w.Write(success); })).ConfigureAwait(false);
                if (success) pending!.Done.TrySetResult((new PeerId(result.Peer), result.Response));
                else { pending?.Done.TrySetResult(null); Interlocked.Increment(ref _rejected); }
            }
            else if (op == RelayOp.Data)
            {
                var data = RelayProtocol.ParseData(bytes, true, _options!.MaxMessageBytes);
                Connection? target;
                lock (_gate) room.Peers.TryGetValue(data.Peer!.Value, out target);
                if (target == null || target.Leaving) { Interlocked.Increment(ref _rejected); return; }
                // A host can send immediately on admission. Its acknowledgement and the
                // client's response travel on different sockets; do not overtake the response.
                if (target.AdmissionDelivered != null && !await target.AdmissionDelivered.Task.ConfigureAwait(false))
                { Interlocked.Increment(ref _rejected); return; }
                await target.Socket.SendData(data.Bytes).ConfigureAwait(false);
                Interlocked.Increment(ref _messages); Interlocked.Add(ref _bytes, data.Bytes.Length);
            }
            else if (op == RelayOp.Retired)
            {
                var peer = RelayProtocol.Parse(bytes, r => RelayProtocol.Text(r, 128));
                Connection? client;
                lock (_gate)
                {
                    room.Peers.TryGetValue(new PeerId(peer), out client);
                    if (client != null && client.Leaving) { room.Peers.Remove(new PeerId(peer)); client.Peer = null; }
                    else client = null;
                }
                if (client != null) { _udp.Remove(client.UdpIdentity); await client.Socket.Send(RelayOp.Ack, Array.Empty<byte>()).ConfigureAwait(false); }
            }
            else if (op == RelayOp.UdpRotate && bytes.Length == 0) await GrantUdp(c).ConfigureAwait(false);
            else throw new InvalidDataException("Invalid host frame");
        }
        private async Task ClientFrame(Connection c, RelayOp op, ReadOnlyMemory<byte> bytes)
        {
            var room = c.Room!;
            if (op == RelayOp.Admit)
            {
                string ticket = RelayProtocol.Parse(bytes, r => RelayProtocol.Text(r));
                long id = Interlocked.Increment(ref _nextRequest);
                Pending pending;
                lock (_gate)
                {
                    if (!room.Active || c.Peer.HasValue || c.Pending || c.Leaving || string.IsNullOrWhiteSpace(ticket) ||
                        room.Peers.Count + _pending.Values.Count(p => p.Room == room) >= _options!.MaxClientsPerRoom) throw new InvalidDataException("Admission unavailable");
                    c.Pending = true; c.Request = id;
                    c.AdmissionDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = new Pending { Room = room, Client = c }; _pending.Add(id, pending);
                }
                await room.Host.Socket.Send(RelayOp.AdmissionRequest, RelayProtocol.Pack(w => { w.Write(id); w.Write(ticket); })).ConfigureAwait(false);
                var completed = await Task.WhenAny(pending.Done.Task, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
                if (completed != pending.Done.Task)
                {
                    lock (_gate) { _pending.Remove(id); c.Pending = false; }
                    await c.Socket.Send(RelayOp.AdmissionReply, RelayProtocol.Pack(w => { w.Write(false); w.Write("{\"error\":\"Admission timed out\"}"); })).ConfigureAwait(false);
                    c.AdmissionDelivered.TrySetResult(true);
                    return;
                }
                var result = await pending.Done.Task.ConfigureAwait(false);
                await c.Socket.Send(RelayOp.AdmissionReply, RelayProtocol.Pack(w => { w.Write(result.HasValue); w.Write(result?.Response ?? "{\"error\":\"Admission denied\"}"); })).ConfigureAwait(false);
                c.AdmissionDelivered!.TrySetResult(true);
                lock (_gate) c.Pending = false;
                if (result.HasValue) await GrantUdp(c).ConfigureAwait(false);
            }
            else if (op == RelayOp.Data)
            {
                var data = RelayProtocol.ParseData(bytes, false, _options!.MaxMessageBytes).Bytes;
                PeerId? peer;
                lock (_gate) peer = c.Peer.HasValue && !c.Leaving && room.Active ? c.Peer : null;
                if (!peer.HasValue) { Interlocked.Increment(ref _rejected); return; }
                await room.Host.Socket.SendData(data, peer.Value.Value).ConfigureAwait(false);
                Interlocked.Increment(ref _messages); Interlocked.Add(ref _bytes, data.Length);
            }
            else if ((op == RelayOp.Prepared || op == RelayOp.DirectoryReady) && bytes.Length == 0)
            {
                PeerId? peer;
                lock (_gate) peer = c.Peer.HasValue && !c.Leaving ? c.Peer : null;
                if (!peer.HasValue) { Interlocked.Increment(ref _rejected); return; }
                await room.Host.Socket.Send(op, RelayProtocol.Pack(w => w.Write(peer.Value.Value))).ConfigureAwait(false);
            }
            else if (op == RelayOp.Leave && bytes.Length == 0)
            {
                PeerId? peer;
                lock (_gate) { peer = c.Peer; if (peer.HasValue && !c.Leaving) c.Leaving = true; else peer = null; }
                if (peer.HasValue) { _udp.Remove(c.UdpIdentity); await room.Host.Socket.Send(RelayOp.Retire, RelayProtocol.Pack(w => w.Write(peer.Value.Value))).ConfigureAwait(false); }
                else throw new InvalidDataException("Invalid leave");
            }
            else if (op == RelayOp.UdpRotate && bytes.Length == 0 && c.Peer.HasValue && !c.Leaving) await GrantUdp(c).ConfigureAwait(false);
            else throw new InvalidDataException("Invalid client frame");
        }
        private async Task Disconnect(Connection c)
        {
            if (Interlocked.Exchange(ref c.Released, 1) != 0) return;
            c.Socket.Dispose();
            c.AdmissionDelivered?.TrySetResult(false);
            Room? close = null;
            Connection[] clients = Array.Empty<Connection>();
            PeerId? departing = null;
            var revoke = new List<PeerId>();
            lock (_gate)
            {
                _connections.Remove(c);
                var room = c.Room;
                if (room != null) { revoke.Add(c.UdpIdentity); _udpRouting.Remove(c.UdpIdentity); }
                if (room != null && c.IsHost && _rooms.TryGetValue(room.Id, out var found) && found == room)
                {
                    _rooms.Remove(room.Id); _closingRooms.Add(room.Id); room.Active = false; close = room;
                    clients = _connections.Where(x => x.Room == room).ToArray();
                    foreach (var entry in _pending.Where(x => x.Value.Room == room).ToArray()) { _pending.Remove(entry.Key); entry.Value.Done.TrySetResult(null); }
                    room.Peers.Clear();
                    foreach (var client in clients) { revoke.Add(client.UdpIdentity); _udpRouting.Remove(client.UdpIdentity); }
                }
                else if (room != null)
                {
                    if (c.Peer.HasValue) { departing = c.Peer; room.Peers.Remove(departing.Value); c.Peer = null; }
                    if (c.Pending && _pending.TryGetValue(c.Request, out var pending)) { _pending.Remove(c.Request); pending.Done.TrySetResult(null); c.Pending = false; }
                }
            }
            foreach (var identity in revoke) _udp.Remove(identity);
            foreach (var client in clients) client.Socket.Dispose();
            if (departing.HasValue && c.Room!.Active)
                try { await c.Room.Host.Socket.Send(RelayOp.Left, RelayProtocol.Pack(w => w.Write(departing.Value.Value))).ConfigureAwait(false); } catch { }
            if (close != null)
            {
                try
                {
                    if (close.ActivationTask != null)
                        try { await close.ActivationTask.ConfigureAwait(false); } catch { }
                    try { await _authorizer.CloseRoomAsync(close.Id, close.Credential, CancellationToken.None).ConfigureAwait(false); } catch { }
                }
                finally { lock (_gate) _closingRooms.Remove(close.Id); }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Connection[] sockets;
            lock (_gate) sockets = _connections.ToArray();
            foreach (var c in sockets) c.Socket.Dispose();
            _udp.Dispose();
            _service.Dispose();
        }
    }
}
