using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.Transport;

namespace BITKit.Multiplayer.TouchSocket
{
    public sealed class RelayHostWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
    {
        private readonly ITransportFactory _transportFactory;
        public RelayHostWire(ITransportFactory? transportFactory = null) => _transportFactory = transportFactory ?? new UdpTransportFactory();
        private UdpLane? _udp;
        private RelayConnectOptions? _udpOptions;
        private TaskCompletionSource<UdpLane.Grant>? _grantReply;
        private int _udpEnabled = 1;
        public int MaxUnreliablePayloadBytes => UdpLane.PayloadMaximum;
        public bool UnreliableEnabled { get => Volatile.Read(ref _udpEnabled) != 0; set { Volatile.Write(ref _udpEnabled, value ? 1 : 0); if (_udp != null) _udp.Enabled = value; } }
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public bool IsUnreliableReady(PeerId peer) { lock (_gate) return _peers.Contains(peer) && _udp?.Ready(_udpOptions!.HostPeerId) == true; }
        public DatagramStatistics GetDatagramStatistics() => _udp?.Statistics() ?? new DatagramStatistics();
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        { lock (_gate) if (!_peers.Contains(peer)) throw new RpcException(RpcError.MissingTarget, "Relay peer unavailable"); return (_udp ?? throw new RpcException(RpcError.Disconnected, "UDP not started")).Send(_udpOptions!.HostPeerId, payload, peer.Value); }
        public async Task RebindDatagramsAsync(CancellationToken cancellationToken = default)
        {
            var options = _udpOptions ?? throw new RpcException(RpcError.Disconnected, "Relay not registered");
            var reply = new TaskCompletionSource<UdpLane.Grant>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _grantReply, reply, null) != null) throw new InvalidOperationException("Rebind in progress");
            var next = NewLane(); var previous = _udp;
            try { await next.Start(0, System.Net.IPAddress.Any, UdpLane.Resolve(options.Address, options.Port)); if (previous != null) previous.Enabled = false; _udp = next;
                await _socket!.Send(RelayOp.UdpRotate, Array.Empty<byte>()); var grant = await UdpLane.WaitFor(reply.Task, cancellationToken);
                await next.Wait(grant, cancellationToken); previous?.Dispose(); }
            catch { _udp = previous; if (previous != null) previous.Enabled = UnreliableEnabled; next.Dispose(); throw; }
            finally { Interlocked.CompareExchange(ref _grantReply, null, reply); }
        }
        private UdpLane NewLane() { var lane = new UdpLane(false, (_, bytes, claimed) =>
        { var peer = new PeerId(claimed); lock (_gate) if (_peers.Contains(peer)) UnreliableReceived?.Invoke(peer, bytes); }, factory: _transportFactory); lane.Enabled = UnreliableEnabled; return lane; }
        private RelaySocket? _socket;
        private Task? _reader;
        private Func<string, Task<(PeerId Peer, string Response)>>? _admission;
        private Action<PeerId>? _admitted, _aborted;
        private readonly object _gate = new object();
        private readonly HashSet<PeerId> _peers = new HashSet<PeerId>();
        private readonly Dictionary<long, PeerId> _awaiting = new Dictionary<long, PeerId>();
        // Left may overtake AdmissionReply on a different server receive task. Keep
        // the tombstone until the result decides whether to retire or abort the commit.
        private readonly HashSet<PeerId> _leftBeforeAck = new HashSet<PeerId>();
        private TaskCompletionSource<bool>? _activation;
        private int _closed;
        public bool IsConnected => _socket != null && Volatile.Read(ref _closed) == 0;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public event Action<PeerId>? PeerPrepared;
        public event Action<PeerId>? PeerDirectoryReady;
        public event Action<string>? AdmissionFailed;
        public async Task ConnectAsync(RelayConnectOptions options, Func<string, Task<(PeerId Peer, string Response)>> admission,
            Action<PeerId> admitted, Action<PeerId>? admissionAborted = null, CancellationToken cancellationToken = default)
        {
            RelayProtocol.CheckOptions(options);
            if (string.IsNullOrWhiteSpace(options.HostCredential) || admission == null || admitted == null) throw new ArgumentException("Host registration requires credential and callbacks");
            if (_socket != null || _closed != 0) throw new InvalidOperationException("Host wire already used");
            var socket = await RelaySocket.Dial(options, cancellationToken).ConfigureAwait(false);
            try
            {
                _udpOptions = options; _udp = NewLane(); await _udp.Start(0, System.Net.IPAddress.Any, UdpLane.Resolve(options.Address, options.Port));
                await socket.Send(RelayOp.Register, RelayProtocol.Pack(w => { w.Write(options.VerifyToken); w.Write(options.RoomId); w.Write(options.Scope); w.Write(options.HostPeerId.Value); w.Write(options.HostCredential); })).ConfigureAwait(false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(7));
                    using var ack = await socket.Read(timeout.Token).ConfigureAwait(false);
                    if (ack.Op != RelayOp.Ack || !RelayProtocol.Parse(ack.Payload, r => r.ReadBoolean())) throw new InvalidDataException("Host registration denied");
                }
                _admission = admission; _admitted = admitted; _aborted = admissionAborted; _socket = socket;
                _reader = ReadLoop();
            }
            catch { _udp?.Dispose(); socket.Dispose(); throw; }
        }
        public async Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            var socket = _socket ?? throw new RpcException(RpcError.Disconnected, "Host not registered");
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { if (_activation != null) throw new InvalidOperationException("Activation already requested"); _activation = reply; }
            await socket.Send(RelayOp.Activate, Array.Empty<byte>()).ConfigureAwait(false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using (timeout.Token.Register(() => reply.TrySetCanceled()))
                    if (!await reply.Task.ConfigureAwait(false)) throw new InvalidOperationException("Relay activation denied");
            }
        }
        private async Task ReadLoop()
        {
            try
            {
                while (true)
                {
                    using var frame = await _socket!.Read(CancellationToken.None).ConfigureAwait(false);
                    if (frame.Op == RelayOp.Ack)
                    {
                        bool ok = RelayProtocol.Parse(frame.Payload, r => r.ReadBoolean());
                        _activation?.TrySetResult(ok);
                    }
                    else if (frame.Op == RelayOp.AdmissionRequest)
                    {
                        var request = RelayProtocol.Parse(frame.Payload, r => (Id: r.ReadInt64(), Ticket: RelayProtocol.Text(r)));
                        // Admission work is bounded by the server's per-room pending limit; the read loop
                        // remains available to process independent game frames and retire notifications.
                        _ = RunAdmission(request.Id, request.Ticket);
                    }
                    else if (frame.Op == RelayOp.AdmissionReply)
                    {
                        var result = RelayProtocol.Parse(frame.Payload, r => (Id: r.ReadInt64(), Accepted: r.ReadBoolean()));
                        PeerId peer;
                        bool left;
                        lock (_gate)
                        {
                            if (!_awaiting.TryGetValue(result.Id, out peer)) continue;
                            _awaiting.Remove(result.Id);
                            left = _leftBeforeAck.Remove(peer);
                            if (result.Accepted && !left) _peers.Add(peer);
                        }
                        if (!result.Accepted) Abort(peer); // rolls back _admitted's completed commit
                        else if (left) { try { PeerLeft?.Invoke(peer); } catch { } }
                    }
                    else if (frame.Op == RelayOp.Data)
                    {
                        var data = RelayProtocol.ParseData(frame.Payload, true, RelayProtocol.AbsoluteLimit);
                        var peer = data.Peer!.Value;
                        lock (_gate) { if (!_peers.Contains(peer)) continue; }
                        try
                        {
                            if (MemoryReceived != null) MemoryReceived(peer, data.Bytes);
                            else if (Received != null) Received(peer, data.Bytes.ToArray());
                        }
                        catch { }
                    }
                    else if (frame.Op == RelayOp.UdpGrant)
                    {
                        var credential = RelayProtocol.Parse(frame.Payload, r => RelayProtocol.Text(r, 128));
                        var grant = _udp?.Install(_udpOptions!.HostPeerId, credential);
                        if (grant != null) _grantReply?.TrySetResult(grant);
                    }
                    else if (frame.Op == RelayOp.Prepared || frame.Op == RelayOp.DirectoryReady || frame.Op == RelayOp.Left || frame.Op == RelayOp.Retire)
                    {
                        var peer = new PeerId(RelayProtocol.Parse(frame.Payload, r => RelayProtocol.Text(r, 128)));
                        if (frame.Op == RelayOp.Prepared) { try { PeerPrepared?.Invoke(peer); } catch { } }
                        else if (frame.Op == RelayOp.DirectoryReady) { try { PeerDirectoryReady?.Invoke(peer); } catch { } }
                        else
                        {
                            bool removed;
                            lock (_gate)
                            {
                                removed = _peers.Remove(peer);
                                if (!removed && _awaiting.ContainsValue(peer)) _leftBeforeAck.Add(peer);
                            }
                            if (removed) { try { PeerLeft?.Invoke(peer); } catch { } }
                            if (frame.Op == RelayOp.Retire) await _socket!.Send(RelayOp.Retired, RelayProtocol.Pack(w => w.Write(peer.Value))).ConfigureAwait(false);
                        }
                    }
                    else throw new InvalidDataException("Invalid host relay control");
                }
            }
            catch { Dispose(); }
        }
        private async Task RunAdmission(long id, string ticket)
        {
            PeerId? peer = null;
            try
            {
                var task = _admission!(ticket);
                var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (completed != task)
                {
                    _ = task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) Abort(t.Result.Peer); }, TaskScheduler.Default);
                    throw new TimeoutException();
                }
                var result = await task.ConfigureAwait(false);
                peer = result.Peer;
                if (string.IsNullOrWhiteSpace(peer.Value.Value) || result.Response == null || System.Text.Encoding.UTF8.GetByteCount(result.Response) > RelayProtocol.TicketLimit) throw new InvalidDataException("Invalid admission result");
                lock (_gate) { if (_closed != 0 || _peers.Contains(peer.Value) || _awaiting.ContainsValue(peer.Value)) throw new InvalidDataException("Duplicate admission"); _awaiting.Add(id, peer.Value); }
                _admitted!(peer.Value); // callback completes before result can be forwarded by relay
                await _socket!.Send(RelayOp.AdmissionResult, RelayProtocol.Pack(w => { w.Write(id); w.Write(peer.Value.Value); w.Write(result.Response); })).ConfigureAwait(false);
                peer = null;
            }
            catch
            {
                try { AdmissionFailed?.Invoke("Admission denied"); } catch { }
                if (peer.HasValue) { lock (_gate) _awaiting.Remove(id); Abort(peer.Value); }
                try { await _socket!.Send(RelayOp.AdmissionResult, RelayProtocol.Pack(w => { w.Write(id); w.Write(""); w.Write("{\"error\":\"Admission denied\"}"); })).ConfigureAwait(false); } catch { }
            }
        }
        private void Abort(PeerId peer)
        {
            lock (_gate) { if (_peers.Contains(peer) || _awaiting.ContainsValue(peer)) return; }
            try { _aborted?.Invoke(peer); } catch { }
        }
        public Task SendAsync(PeerId peer, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            lock (_gate) { if (!_peers.Contains(peer)) throw new RpcException(RpcError.MissingTarget, "Relay peer unavailable"); }
            return (_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).SendData(data, peer.Value);
        }
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            lock (_gate) { if (!_peers.Contains(peer)) throw new RpcException(RpcError.MissingTarget, "Relay peer unavailable"); }
            return new ValueTask((_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).SendData(payload, peer.Value));
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _socket?.Dispose(); _activation?.TrySetException(new RpcException(RpcError.Disconnected, "Relay closed"));
            _udp?.Dispose();
            PeerId[] peers; PeerId[] pending;
            lock (_gate) { peers = new PeerId[_peers.Count]; _peers.CopyTo(peers); _peers.Clear(); pending = new PeerId[_awaiting.Count]; _awaiting.Values.CopyTo(pending, 0); _awaiting.Clear(); _leftBeforeAck.Clear(); }
            foreach (var peer in peers) { try { PeerLeft?.Invoke(peer); } catch { } }
            foreach (var peer in pending) Abort(peer);
        }
    }

    public sealed class RelayClientWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
    {
        private readonly ITransportFactory _transportFactory;
        private UdpLane? _udp;
        private RelayConnectOptions? _udpOptions;
        private TaskCompletionSource<UdpLane.Grant>? _grantReply;
        private int _udpEnabled = 1;
        public int MaxUnreliablePayloadBytes => UdpLane.PayloadMaximum;
        public bool UnreliableEnabled { get => Volatile.Read(ref _udpEnabled) != 0; set { Volatile.Write(ref _udpEnabled, value ? 1 : 0); if (_udp != null) _udp.Enabled = value; } }
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public bool IsUnreliableReady(PeerId peer) => peer.Equals(_host) && _admitted != 0 && _udp?.Ready(_host) == true;
        public DatagramStatistics GetDatagramStatistics() => _udp?.Statistics() ?? new DatagramStatistics();
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) => peer.Equals(_host) && _admitted != 0 && _leaving == 0 ?
            (_udp ?? throw new RpcException(RpcError.Disconnected, "UDP not started")).Send(_host, payload) : throw new RpcException(RpcError.MissingTarget, "Host unavailable");
        public async Task RebindDatagramsAsync(CancellationToken cancellationToken = default)
        {
            var options = _udpOptions ?? throw new RpcException(RpcError.Disconnected, "Relay not connected");
            if (_admitted == 0 || _leaving != 0) throw new RpcException(RpcError.Unauthorized, "Client not admitted");
            var reply = new TaskCompletionSource<UdpLane.Grant>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _grantReply, reply, null) != null) throw new InvalidOperationException("Rebind in progress");
            var next = NewLane(); var previous = _udp;
            try { await next.Start(0, System.Net.IPAddress.Any, UdpLane.Resolve(options.Address, options.Port)); if (previous != null) previous.Enabled = false; _udp = next;
                await _socket!.Send(RelayOp.UdpRotate, Array.Empty<byte>()); var grant = await UdpLane.WaitFor(reply.Task, cancellationToken);
                await next.Wait(grant, cancellationToken); previous?.Dispose(); }
            catch { _udp = previous; if (previous != null) previous.Enabled = UnreliableEnabled; next.Dispose(); throw; }
            finally { Interlocked.CompareExchange(ref _grantReply, null, reply); }
        }
        private UdpLane NewLane() { var lane = new UdpLane(false, (_, bytes, claimed) =>
        { if (claimed == _host.Value && _admitted != 0 && _leaving == 0) UnreliableReceived?.Invoke(_host, bytes); }, factory: _transportFactory); lane.Enabled = UnreliableEnabled; return lane; }
        private readonly PeerId _host;
        private RelaySocket? _socket;
        private TaskCompletionSource<string>? _admission;
        private TaskCompletionSource<bool>? _leave;
        private int _closed, _admitted, _leaving;
        public RelayClientWire(PeerId host, ITransportFactory? transportFactory = null)
        { _host = host; _transportFactory = transportFactory ?? new UdpTransportFactory(); }
        public bool IsConnected => _socket != null && Volatile.Read(ref _closed) == 0;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public async Task ConnectAsync(RelayConnectOptions options, CancellationToken cancellationToken = default)
        {
            RelayProtocol.CheckOptions(options);
            if (_host.Value != options.HostPeerId.Value || _socket != null || _closed != 0) throw new ArgumentException("Host identity mismatch or wire already used");
            var socket = await RelaySocket.Dial(options, cancellationToken).ConfigureAwait(false);
            try
            {
                _udpOptions = options; _udp = NewLane(); await _udp.Start(0, System.Net.IPAddress.Any, UdpLane.Resolve(options.Address, options.Port));
                await socket.Send(RelayOp.Select, RelayProtocol.Pack(w => { w.Write(options.VerifyToken); w.Write(options.RoomId); w.Write(options.Scope); w.Write(options.HostPeerId.Value); })).ConfigureAwait(false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(7));
                    using var ack = await socket.Read(timeout.Token).ConfigureAwait(false);
                    if (ack.Op != RelayOp.Ack || !RelayProtocol.Parse(ack.Payload, r => r.ReadBoolean())) throw new InvalidDataException("Relay room unavailable");
                }
                _socket = socket; _ = ReadLoop();
            }
            catch { _udp?.Dispose(); socket.Dispose(); throw; }
        }
        private async Task ReadLoop()
        {
            try
            {
                while (true)
                {
                    using var frame = await _socket!.Read(CancellationToken.None).ConfigureAwait(false);
                    if (frame.Op == RelayOp.AdmissionReply)
                    {
                        var response = RelayProtocol.Parse(frame.Payload, r => (Accepted: r.ReadBoolean(), Text: RelayProtocol.Text(r)));
                        if (Volatile.Read(ref _leaving) == 0)
                            Interlocked.Exchange(ref _admitted, response.Accepted ? 1 : 0);
                        _admission?.TrySetResult(response.Text);
                    }
                    else if (frame.Op == RelayOp.Ack && frame.Payload.Length == 0) _leave?.TrySetResult(true);
                    else if (frame.Op == RelayOp.Data)
                    {
                        var bytes = RelayProtocol.ParseData(frame.Payload, false, RelayProtocol.AbsoluteLimit).Bytes;
                        if (_admitted != 0 && Volatile.Read(ref _leaving) == 0)
                        {
                            try
                            {
                                if (MemoryReceived != null) MemoryReceived(_host, bytes);
                                else if (Received != null) Received(_host, bytes.ToArray());
                            }
                            catch { }
                        }
                    }
                    else if (frame.Op == RelayOp.UdpGrant)
                    {
                        var credential = RelayProtocol.Parse(frame.Payload, r => RelayProtocol.Text(r, 128));
                        var grant = _udp?.Install(_host, credential);
                        if (grant != null) _grantReply?.TrySetResult(grant);
                    }
                    else throw new InvalidDataException("Invalid relay control");
                }
            }
            catch { Dispose(); }
        }
        public async Task<string> RequestAdmissionAsync(string ticket, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(ticket) || System.Text.Encoding.UTF8.GetByteCount(ticket) > RelayProtocol.TicketLimit) throw new ArgumentException("Invalid ticket", nameof(ticket));
            if (Volatile.Read(ref _admitted) != 0 || Volatile.Read(ref _leaving) != 0)
                throw new InvalidOperationException("Admission already completed or client is leaving");
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _admission, reply, null) != null)
                throw new InvalidOperationException("Admission already in progress");
            try
            {
                // Recheck after claiming the slot: a reply or leave may race the initial check.
                if (Volatile.Read(ref _admitted) != 0 || Volatile.Read(ref _leaving) != 0)
                    throw new InvalidOperationException("Admission already completed or client is leaving");
                await (_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).Send(RelayOp.Admit, RelayProtocol.Pack(w => w.Write(ticket))).ConfigureAwait(false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using (timeout.Token.Register(() => { reply.TrySetCanceled(); Dispose(); })) return await reply.Task.ConfigureAwait(false);
                }
            }
            finally { Interlocked.CompareExchange(ref _admission, null, reply); }
        }
        public Task SendAsync(PeerId peer, byte[] data)
        {
            if (!peer.Equals(_host)) throw new RpcException(RpcError.MissingTarget, "Clients only route to their Host");
            if (_admitted == 0 || Volatile.Read(ref _leaving) != 0) throw new RpcException(RpcError.Unauthorized, "Client not admitted or leaving");
            return (_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).SendData(data);
        }
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            if (!peer.Equals(_host)) throw new RpcException(RpcError.MissingTarget, "Clients only route to their Host");
            if (_admitted == 0 || Volatile.Read(ref _leaving) != 0) throw new RpcException(RpcError.Unauthorized, "Client not admitted or leaving");
            return new ValueTask((_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).SendData(payload));
        }
        public Task NotifyPreparedAsync() => Control(RelayOp.Prepared);
        public Task NotifyDirectoryReadyAsync() => Control(RelayOp.DirectoryReady);
        private Task Control(RelayOp op) => _admitted == 0 || Volatile.Read(ref _leaving) != 0 ? throw new RpcException(RpcError.Unauthorized, "Client not admitted or leaving") :
            (_socket ?? throw new RpcException(RpcError.Disconnected, "Relay closed")).Send(op, Array.Empty<byte>());
        public async Task NotifyLeavingAsync(CancellationToken token = default)
        {
            if (!IsConnected || _admitted == 0 || Volatile.Read(ref _leaving) == 2) return;
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _leave, reply, null) != null) throw new InvalidOperationException("Leave already pending");
            try
            {
                if (Interlocked.CompareExchange(ref _leaving, 1, 0) != 0)
                    throw new InvalidOperationException("Leave already pending");
                await _socket!.Send(RelayOp.Leave, Array.Empty<byte>()).ConfigureAwait(false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using (timeout.Token.Register(() => reply.TrySetCanceled())) await reply.Task.ConfigureAwait(false);
                }
                // ACK arrives only after Host has retired this peer. Like the direct
                // wire, keep transport alive until RpcRuntime unsubscribes and its
                // caller disposes it; never signal an artificial Host loss here.
                Interlocked.Exchange(ref _admitted, 0);
                _udp?.Dispose();
                Interlocked.Exchange(ref _leaving, 2);
            }
            finally { Interlocked.CompareExchange(ref _leave, null, reply); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _socket?.Dispose();
            _udp?.Dispose();
            _admission?.TrySetException(new RpcException(RpcError.Disconnected, "Relay closed"));
            _leave?.TrySetException(new RpcException(RpcError.Disconnected, "Relay closed"));
            if (Volatile.Read(ref _leaving) != 2)
                try { PeerLeft?.Invoke(_host); } catch { }
        }
    }
}
