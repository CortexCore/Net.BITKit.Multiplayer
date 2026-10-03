using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.Transport;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Sockets;

namespace BITKit.Multiplayer.TouchSocket
{

// The host explicitly admits authenticated sessions. A socket's asserted packet sender is never trusted.
public sealed class TouchSocketHostWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
{
    private const ushort Protocol = 200;
    private readonly ConcurrentDictionary<string, PeerId> _sessions = new();
    private readonly ConcurrentDictionary<PeerId, string> _peers = new();
    private readonly ConcurrentDictionary<string, int> _pendingSessions = new();
    private readonly System.Collections.Generic.HashSet<PeerId> _leavingPeers = new();
    private readonly object _admissionGate = new();
    private readonly TcpDmtpService _service = new();
    private readonly UdpLane _udp;
    public TouchSocketHostWire(ITransportFactory? transportFactory = null)
    { _udp = new UdpLane(true, (peer, bytes, _) => UnreliableReceived?.Invoke(peer, bytes), factory: transportFactory); }
    public int MaxUnreliablePayloadBytes => UdpLane.PayloadMaximum;
    public bool UnreliableEnabled { get => _udp.Enabled; set => _udp.Enabled = value; }
    public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
    public bool IsUnreliableReady(PeerId peer) => _udp.Ready(peer);
    public DatagramStatistics GetDatagramStatistics() => _udp.Statistics();
    public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) => _udp.Send(peer, payload);
    public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("A listening Host endpoint cannot be rebound");
    private bool _running;
    private Func<string, Task<(PeerId Peer, string Response)>>? _admission;
    private Action<PeerId>? _admitted;
    private Action<PeerId>? _admissionAborted;
    public event Action<string>? AdmissionFailed;
    public bool IsConnected => Volatile.Read(ref _running);
    public event Action<PeerId, byte[]>? Received;
    public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
    public event Action<PeerId>? PeerLeft;
    public event Action<PeerId>? PeerPrepared;
    public event Action<PeerId>? PeerDirectoryReady;
    // Loopback remains the safe default. Direct mode has no built-in TLS configuration:
    // never expose its credential-bearing reliable control plane publicly without a trusted TLS tunnel.
    public async Task StartAsync(int port, string verifyToken, IPAddress? bindAddress = null,
        Func<string, Task<(PeerId Peer, string Response)>>? admission = null, Action<PeerId>? admitted = null,
        Action<PeerId>? admissionAborted = null)
    {
        _admission = admission;
        _admitted = admitted;
        _admissionAborted = admissionAborted;
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(verifyToken)) throw new ArgumentException("Protocol token required", nameof(verifyToken));
        bindAddress ??= IPAddress.Loopback;
        if (bindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Specify an IPv4 bind address", nameof(bindAddress));
        try { await _service.SetupAsync(new TouchSocketConfig()
            .ConfigurePlugins(plugins =>
            {
                plugins.AddDmtpReceivedPlugin((IDmtpActorObject actor, DmtpMessageEventArgs e) =>
                {
                    if (e.DmtpMessage.ProtocolFlags == 201 && actor is ITcpDmtpSessionClient incoming && _admission != null)
                        return HandleAdmission(incoming, e.DmtpMessage.Memory.ToArray());
                    if (e.DmtpMessage.ProtocolFlags == 203 && actor is ITcpDmtpSessionClient leaving && _sessions.TryGetValue(leaving.Id, out var departing))
                        return AcknowledgeLeave(leaving, departing);
                    if (e.DmtpMessage.ProtocolFlags == 204 && e.DmtpMessage.Memory.Length == 0 && actor is ITcpDmtpSessionClient prepared && _sessions.TryGetValue(prepared.Id, out var preparing))
                    { PeerPrepared?.Invoke(preparing); return Task.CompletedTask; }
                    if (e.DmtpMessage.ProtocolFlags == 205 && e.DmtpMessage.Memory.Length == 0 && actor is ITcpDmtpSessionClient ready && _sessions.TryGetValue(ready.Id, out var confirmed))
                    { PeerDirectoryReady?.Invoke(confirmed); return Task.CompletedTask; }
                    if (e.DmtpMessage.ProtocolFlags == 208 && e.DmtpMessage.Memory.Length == 0 && actor is ITcpDmtpSessionClient rebinding && _sessions.TryGetValue(rebinding.Id, out var rebindingPeer))
                        return Grant(rebinding, rebindingPeer);
                    if (e.DmtpMessage.ProtocolFlags == Protocol && actor is ITcpDmtpSessionClient session && _sessions.TryGetValue(session.Id, out var peer))
                    {
                        if (MemoryReceived != null) MemoryReceived(peer, e.DmtpMessage.Memory);
                        else if (Received != null) Received(peer, e.DmtpMessage.Memory.ToArray());
                    }
                    return Task.CompletedTask;
                });
                plugins.AddDmtpClosedPlugin((IDmtpActorObject actor) =>
                {
                    if (actor is ITcpDmtpSessionClient session)
                    {
                        lock (_admissionGate) _pendingSessions.TryUpdate(session.Id, 1, 0);
                        if (_sessions.TryGetValue(session.Id, out var peer)) Remove(peer);
                    }
                });
                // Physical TCP closure (including abrupt disposal) need not generate a DMTP
                // close frame. Remove the authenticated mapping on either lifecycle path.
                plugins.AddTcpClosedPlugin((ITcpSession actor) =>
                {
                    if (actor is ITcpDmtpSessionClient session)
                    {
                        lock (_admissionGate) _pendingSessions.TryUpdate(session.Id, 1, 0);
                        if (_sessions.TryGetValue(session.Id, out var peer)) Remove(peer);
                    }
                });
            })
            .SetDmtpOption(option => option.VerifyToken = verifyToken)
            .SetListenIPHosts(new IPHost(bindAddress + ":" + port)));
        await _udp.Start(port, bindAddress); await _service.StartAsync(); }
        catch { _udp.Dispose(); _service.Dispose(); throw; }
        lock (_admissionGate) _running = true;
    }
    private async Task AcknowledgeLeave(ITcpDmtpSessionClient session, PeerId peer)
    {
        // Remove fires PeerLeft synchronously: RpcRuntime has retired the session before ACK.
        Remove(peer);
        await session.DmtpActor.SendAsync(206, Array.Empty<byte>());
    }
    private async Task HandleAdmission(ITcpDmtpSessionClient session, byte[] payload)
    {
        if (payload.Length is < 1 or > 4096 || _sessions.ContainsKey(session.Id) || !_pendingSessions.TryAdd(session.Id, 0)) { await RejectAdmission(session, "Admission denied"); return; }
        PeerId? pendingPeer = null;
        PeerId? admittedPeer = null;
        try
        {
            var admission = _admission!(Encoding.UTF8.GetString(payload));
            PeerId peer; string response;
            try { (peer, response) = await admission.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                _ = admission.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) AbortAdmission(t.Result.Peer); }, TaskScheduler.Default);
                throw;
            }
            pendingPeer = peer;
            if (Encoding.UTF8.GetByteCount(response) > 4096) throw new InvalidOperationException("Admission response too large");
            Admit(peer, session.Id);
            _admitted?.Invoke(peer);
            pendingPeer = null;
            admittedPeer = peer;
            await session.DmtpActor.SendAsync(202, Encoding.UTF8.GetBytes(response));
            await Grant(session, peer);
        }
        catch (Exception ex)
        {
            if (pendingPeer.HasValue) { Remove(pendingPeer.Value); AbortAdmission(pendingPeer.Value); }
            if (admittedPeer.HasValue) Remove(admittedPeer.Value);
            await RejectAdmission(session, ex is TimeoutException ? "Admission timed out" : "Admission denied");
        }
        finally { _pendingSessions.TryRemove(session.Id, out _); }
    }
    private void AbortAdmission(PeerId peer) { try { _admissionAborted?.Invoke(peer); } catch { } }
    private async Task Grant(ITcpDmtpSessionClient session, PeerId peer)
    {
        var credential = UdpLane.NewCredential();
        lock (_admissionGate)
        {
            if (!_running || !_sessions.TryGetValue(session.Id, out var current) || !current.Equals(peer) || _leavingPeers.Contains(peer)) return;
            _udp.Install(peer, credential);
        }
        try { await session.DmtpActor.SendAsync(207, Encoding.ASCII.GetBytes(credential)); }
        catch { _udp.Remove(peer); throw; }
    }
    private async Task RejectAdmission(ITcpDmtpSessionClient session, string reason)
    {
        try { AdmissionFailed?.Invoke(reason); } catch { }
        try { await session.DmtpActor.SendAsync(202, Encoding.UTF8.GetBytes("{\"error\":\"" + reason + "\"}")); } catch { }
    }
    public void Admit(PeerId peer, string sessionId)
    {
        lock (_admissionGate)
        {
            if (!_running || _pendingSessions.TryGetValue(sessionId, out var closed) && closed != 0) throw new InvalidOperationException("Session is closed");
            if (!_service.Clients.TryGetClient(sessionId, out _)) throw new InvalidOperationException("No verified session");
            if (_sessions.ContainsKey(sessionId) || _peers.ContainsKey(peer) || _leavingPeers.Contains(peer)) throw new InvalidOperationException("Duplicate admission");
            // No other admission/removal can interleave between the two index updates.
            _sessions[sessionId] = peer;
            _peers[peer] = sessionId;
        }
    }
    public async Task SendAsync(PeerId peer, byte[] data)
    {
        if (!_peers.TryGetValue(peer, out var id) || !_service.Clients.TryGetClient(id, out var session)) throw new RpcException(RpcError.Disconnected, "Session unavailable");
        await session.DmtpActor.SendAsync(Protocol, data);
    }
    public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
    {
        if (!_peers.TryGetValue(peer, out var id) || !_service.Clients.TryGetClient(id, out var session))
            throw new RpcException(RpcError.Disconnected, "Session unavailable");
        return new ValueTask(session.DmtpActor.SendAsync(Protocol, payload));
    }
    public void Remove(PeerId peer)
    {
        string id;
        lock (_admissionGate)
        {
            if (!_peers.TryGetValue(peer, out id!) || !_leavingPeers.Add(peer)) return;
            _udp.Remove(peer);
        }
        try { PeerLeft?.Invoke(peer); }
        finally
        {
            lock (_admissionGate)
            {
                _peers.TryRemove(peer, out _);
                _sessions.TryRemove(id, out _);
                _leavingPeers.Remove(peer);
            }
        }
    }
    public void Dispose()
    {
        lock (_admissionGate) _running = false;
        foreach (var peer in _peers.Keys) Remove(peer);
        _service.Dispose();
        _udp.Dispose();
    }
}

public sealed class TouchSocketClientWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
{
    private const ushort Protocol = 200;
    private readonly TcpDmtpClient _client = new();
    private readonly PeerId _host;
    private readonly ITransportFactory _transportFactory;
    private UdpLane? _udp;
    private string? _udpHost;
    private int _udpPort;
    private TaskCompletionSource<UdpLane.Grant>? _grantReply;
    private int _udpEnabled = 1;
    public int MaxUnreliablePayloadBytes => UdpLane.PayloadMaximum;
    public bool UnreliableEnabled { get => Volatile.Read(ref _udpEnabled) != 0; set { Volatile.Write(ref _udpEnabled, value ? 1 : 0); if (_udp != null) _udp.Enabled = value; } }
    public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
    public bool IsUnreliableReady(PeerId peer) => peer.Equals(_host) && _udp?.Ready(_host) == true;
    public DatagramStatistics GetDatagramStatistics() => _udp?.Statistics() ?? new DatagramStatistics();
    public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) => peer.Equals(_host) ? (_udp ?? throw new RpcException(RpcError.Disconnected, "UDP not started")).Send(peer, payload) : throw new RpcException(RpcError.MissingTarget, "Host only");
    private TaskCompletionSource<string>? _admissionReply;
    private TaskCompletionSource<bool>? _leaveReply;
    public TouchSocketClientWire(PeerId host, ITransportFactory? transportFactory = null)
    { _host = host; _transportFactory = transportFactory ?? new UdpTransportFactory(); }
    public string SessionId => _client.Id;
    public bool IsConnected => _client.Online;
    public event Action<PeerId, byte[]>? Received;
    public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
    public event Action<PeerId>? PeerLeft;
    public async Task ConnectAsync(string host, int port, string verifyToken)
    {
        await _client.SetupAsync(new TouchSocketConfig()
            .ConfigurePlugins(plugins =>
            {
                plugins.AddDmtpReceivedPlugin((IDmtpActorObject actor, DmtpMessageEventArgs e) =>
                {
                    if (e.DmtpMessage.ProtocolFlags == 202 && ReferenceEquals(actor, _client) && e.DmtpMessage.Memory.Length <= 4096)
                        _admissionReply?.TrySetResult(Encoding.UTF8.GetString(e.DmtpMessage.Memory.ToArray()));
                    if (e.DmtpMessage.ProtocolFlags == 206 && ReferenceEquals(actor, _client) && e.DmtpMessage.Memory.Length == 0)
                        _leaveReply?.TrySetResult(true);
                    if (e.DmtpMessage.ProtocolFlags == 207 && ReferenceEquals(actor, _client) && e.DmtpMessage.Memory.Length == 64)
                    {
                        try { var grant = _udp?.Install(_host, Encoding.ASCII.GetString(e.DmtpMessage.Memory.ToArray())); if (grant != null) _grantReply?.TrySetResult(grant); } catch { }
                    }
                    if (e.DmtpMessage.ProtocolFlags == Protocol && ReferenceEquals(actor, _client))
                    {
                        if (MemoryReceived != null) MemoryReceived(_host, e.DmtpMessage.Memory);
                        else if (Received != null) Received(_host, e.DmtpMessage.Memory.ToArray());
                    }
                    return Task.CompletedTask;
                });
                plugins.AddDmtpClosedPlugin((IDmtpActorObject actor) => { if (ReferenceEquals(actor, _client)) { _udp?.Dispose(); PeerLeft?.Invoke(_host); } });
                plugins.AddTcpClosedPlugin((ITcpSession actor) => { if (ReferenceEquals(actor, _client)) { _udp?.Dispose(); PeerLeft?.Invoke(_host); } });
            })
            .SetDmtpOption(option => option.VerifyToken = verifyToken)
            .SetRemoteIPHost(host + ":" + port));
        _udpHost = host; _udpPort = port;
        try
        {
            _udp = new UdpLane(false, (peer, bytes, _) => UnreliableReceived?.Invoke(peer, bytes), factory: _transportFactory); _udp.Enabled = UnreliableEnabled;
            await _udp.Start(0, IPAddress.Any, UdpLane.Resolve(host, port));
            await _client.ConnectAsync();
        }
        catch { _udp?.Dispose(); _client.Dispose(); throw; }
    }
    public async Task<string> RequestAdmissionAsync(string request, CancellationToken token = default)
    {
        if (Encoding.UTF8.GetByteCount(request) is < 1 or > 4096) throw new ArgumentException("Admission request must be 1..4096 bytes", nameof(request));
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _admissionReply, reply, null) != null) throw new InvalidOperationException("Admission already pending");
        try
        {
            await _client.DmtpActor.SendAsync(201, Encoding.UTF8.GetBytes(request));
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
        }
        finally { Interlocked.CompareExchange(ref _admissionReply, null, reply); }
    }
    public Task SendAsync(PeerId peer, byte[] data) => peer.Equals(_host) ? _client.DmtpActor.SendAsync(Protocol, data) : throw new RpcException(RpcError.MissingTarget, "Clients only connect to Host");
    public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload) => peer.Equals(_host) ?
        new ValueTask(_client.DmtpActor.SendAsync(Protocol, payload)) : throw new RpcException(RpcError.MissingTarget, "Clients only connect to Host");
    public Task NotifyPreparedAsync() => _client.DmtpActor.SendAsync(204, Array.Empty<byte>());
    public Task NotifyDirectoryReadyAsync() => _client.DmtpActor.SendAsync(205, Array.Empty<byte>());
    public async Task RebindDatagramsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _udpHost == null) throw new RpcException(RpcError.Disconnected, "Reliable connection required");
        var reply = new TaskCompletionSource<UdpLane.Grant>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _grantReply, reply, null) != null) throw new InvalidOperationException("Rebind in progress");
        var next = new UdpLane(false, (peer, bytes, _) => UnreliableReceived?.Invoke(peer, bytes), factory: _transportFactory); next.Enabled = UnreliableEnabled;
        var previous = _udp;
        try
        {
            await next.Start(0, IPAddress.Any, UdpLane.Resolve(_udpHost, _udpPort));
            // New socket must receive the new grant, not the old one.
            if (previous != null) previous.Enabled = false;
            _udp = next;
            await _client.DmtpActor.SendAsync(208, Array.Empty<byte>());
            var grant = await reply.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            await next.Wait(grant, cancellationToken);
            previous?.Dispose();
        }
        catch { _udp = previous; if (previous != null) previous.Enabled = UnreliableEnabled; next.Dispose(); throw; }
        finally { Interlocked.CompareExchange(ref _grantReply, null, reply); }
    }
    // Same-socket voluntary leave; the Host resolves the physical session, never a claimed peer ID.
    public async Task NotifyLeavingAsync(CancellationToken token = default)
    {
        if (!IsConnected) return;
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _leaveReply, reply, null) != null) throw new InvalidOperationException("Leave already pending");
        try
        {
            await _client.DmtpActor.SendAsync(203, Array.Empty<byte>());
            await reply.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
        }
        finally { Interlocked.CompareExchange(ref _leaveReply, null, reply); }
    }
    public void Dispose() { _udp?.Dispose(); _client.Dispose(); PeerLeft?.Invoke(_host); }
}
}
