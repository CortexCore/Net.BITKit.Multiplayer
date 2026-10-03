using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using LiteNetLib;
using RpcTransport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.LiteNetLibDirect
{
    /// <summary>One connection generation. Send completion means LiteNetLib has copied the entire payload into its own packet(s), not that the remote acknowledged it.</summary>
    public sealed class LiteNetLibTransport : RpcTransport, ITransportLifetime, IAsyncDisposable
    {
        private readonly LiteNetLibEndpoint _owner;
        private readonly NetPeer _peer;
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _early = new Queue<byte[]>();
        private Action<ReadOnlyMemory<byte>>? _received;
        private Action? _closedHandlers;
        private bool _closed;
        internal LiteNetLibTransport(LiteNetLibEndpoint owner, NetPeer peer) { _owner = owner; _peer = peer; }
        internal NetPeer Peer => _peer;
        public event Action<ReadOnlyMemory<byte>>? OnReceived
        {
            add { lock (_gate) { if (_closed) return; _received += value; } _owner.ScheduleDrain(this); }
            remove { lock (_gate) _received -= value; }
        }
        public event Action? Closed
        {
            add { bool notify; lock (_gate) { notify = _closed; if (!notify) _closedHandlers += value; } if (notify) value?.Invoke(); }
            remove { lock (_gate) _closedHandlers -= value; }
        }
        public int UnreliablePayloadLimit => _peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => SendCore(payload, DeliveryMethod.ReliableOrdered, cancellationToken);
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => SendCore(payload, DeliveryMethod.Unreliable, cancellationToken);
        private UniTask SendCore(ReadOnlyMemory<byte> payload, DeliveryMethod delivery, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length == 0 || payload.Length > NetRpcCodec.HeaderBytes + NetRpcCodec.MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(payload), "Invalid NetRpc frame size.");
            lock (_gate)
            {
                if (_closed || _peer.ConnectionState != ConnectionState.Connected) throw new InvalidOperationException("LiteNetLib peer disconnected.");
                if (delivery == DeliveryMethod.Unreliable && payload.Length > UnreliablePayloadLimit)
                    throw new ArgumentOutOfRangeException(nameof(payload), $"Unreliable payload exceeds negotiated MTU limit {UnreliablePayloadLimit}.");
                // 1.3.5 NetPeer.Send(ReadOnlySpan<byte>, DeliveryMethod) copies every fragment
                // synchronously into pooled NetPackets before returning. No application loan survives this call.
                _peer.Send(payload.Span, delivery);
            }
            return default;
        }
        internal void Deliver(ReadOnlyMemory<byte> bytes)
        {
            DrainEarly(); // same poll owner, before any newer borrowed frame
            Action<ReadOnlyMemory<byte>>? receiver;
            lock (_gate)
            {
                if (_closed) return;
                receiver = _received;
                if (receiver == null)
                {
                    if (_early.Count < 32) _early.Enqueue(bytes.ToArray()); // accepted before Runtime.AttachPeer
                    else _peer.Disconnect();
                }
            }
            receiver?.Invoke(bytes);
        }
        internal void DrainEarly()
        {
            while (true)
            {
                Action<ReadOnlyMemory<byte>>? receiver; byte[] bytes;
                lock (_gate)
                {
                    if (_closed || _received == null || _early.Count == 0) return;
                    receiver = _received; bytes = _early.Dequeue();
                }
                receiver(bytes);
            }
        }
        internal void End()
        {
            Action? closed;
            lock (_gate) { if (_closed) return; _closed = true; closed = _closedHandlers; _received = null; _early.Clear(); _closedHandlers = null; }
            Exception? failure = null;
            if (closed != null) foreach (Action handler in closed.GetInvocationList())
                try { handler(); } catch (Exception error) { failure ??= error; }
            if (failure != null) throw failure;
        }
        public ValueTask DisposeAsync()
        {
            _owner.Disconnect(_peer);
            return default;
        }
    }

    /// <summary>Owns exactly one NetManager and one PollEvents task. Server sessions own this listener; client sessions own their endpoint.</summary>
    public sealed class LiteNetLibEndpoint : IAsyncDisposable
    {
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _manager;
        private readonly ConcurrentDictionary<NetPeer, LiteNetLibTransport> _peers = new ConcurrentDictionary<NetPeer, LiteNetLibTransport>();
        private readonly ConcurrentQueue<LiteNetLibTransport> _accepted = new ConcurrentQueue<LiteNetLibTransport>();
        private readonly ConcurrentQueue<LiteNetLibTransport> _drains = new ConcurrentQueue<LiteNetLibTransport>();
        [ThreadStatic] private static LiteNetLibEndpoint? _dispatchOwner;
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly object _pollGate = new object();
        private readonly bool _server;
        private UniTask? _poll;
        private UniTaskCompletionSource<LiteNetLibTransport>? _connecting;
        private int _disposed;
        private const string Key = "bitkit-netrpc-direct-v1";
        private LiteNetLibEndpoint(bool server)
        {
            _server = server; _manager = new NetManager(_listener) { AutoRecycle = false };
            _listener.ConnectionRequestEvent += request => { if (_server && Volatile.Read(ref _disposed) == 0) request.AcceptIfKey(Key); else request.Reject(); };
            _listener.PeerConnectedEvent += peer =>
            {
                if (Volatile.Read(ref _disposed) != 0) { peer.Disconnect(); return; }
                var transport = new LiteNetLibTransport(this, peer);
                _peers[peer] = transport;
                if (_server) { _accepted.Enqueue(transport); _available.Release(); }
                else _connecting?.TrySetResult(transport);
            };
            _listener.PeerDisconnectedEvent += (peer, _) =>
            { if (_peers.TryRemove(peer, out var transport)) transport.End(); else _connecting?.TrySetException(new InvalidOperationException("Connection failed.")); };
            _listener.NetworkReceiveEvent += (peer, reader, _, __) =>
            {
                try { if (_peers.TryGetValue(peer, out var transport)) transport.Deliver(new ReadOnlyMemory<byte>(reader.RawData, reader.UserDataOffset, reader.AvailableBytes)); }
                finally { reader.Recycle(); }
            };
        }
        private void Start(int port)
        {
            if (!_manager.Start(port)) throw new InvalidOperationException("Unable to bind LiteNetLib UDP endpoint.");
            _poll = PollLoop().Preserve();
        }
        private async UniTask PollLoop()
        {
            await UniTask.SwitchToThreadPool();
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    lock (_pollGate)
                    {
                        _dispatchOwner = this;
                        try
                        {
                            while (_drains.TryDequeue(out var transport)) transport.DrainEarly();
                            if (!_stopping.IsCancellationRequested) _manager.PollEvents();
                        }
                        finally { _dispatchOwner = null; }
                    }
                    await Task.Delay(2, _stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception error) { _connecting?.TrySetException(error); }
            finally { Shutdown(); }
        }
        public int LocalPort => _manager.LocalPort;
        public static LiteNetLibEndpoint Listen(int port)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var endpoint = new LiteNetLibEndpoint(true);
            try { endpoint.Start(port); return endpoint; }
            catch { endpoint._manager.Stop(); throw; }
        }
        public static async UniTask<(LiteNetLibEndpoint Endpoint, LiteNetLibTransport Transport)> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host required.", nameof(host));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = new LiteNetLibEndpoint(false);
            try
            {
                endpoint._connecting = new UniTaskCompletionSource<LiteNetLibTransport>();
                endpoint.Start(0);
                if (endpoint._manager.Connect(host, port, Key) == null) throw new InvalidOperationException("LiteNetLib connection could not be started.");
                using (cancellationToken.Register(() => endpoint._connecting.TrySetCanceled(cancellationToken)))
                    return (endpoint, await endpoint._connecting.Task);
            }
            catch { await endpoint.DisposeUniTaskAsync(); throw; }
        }
        public async UniTask<LiteNetLibTransport> AcceptAsync(CancellationToken cancellationToken = default)
        {
            if (!_server) throw new InvalidOperationException("Only a listener accepts connections.");
            while (true)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                await _available.WaitAsync(linked.Token).ConfigureAwait(false);
                if (_accepted.TryDequeue(out var transport) && _peers.ContainsKey(transport.Peer)) return transport;
            }
        }
        internal void Disconnect(NetPeer peer)
        {
            lock (_pollGate)
            {
                var previous = _dispatchOwner; _dispatchOwner = this;
                try { if (_peers.TryRemove(peer, out var transport)) transport.End(); }
                finally
                {
                    try { if (Volatile.Read(ref _disposed) == 0 && peer.ConnectionState == ConnectionState.Connected) peer.Disconnect(); }
                    finally { _dispatchOwner = previous; }
                }
            }
        }
        internal void ScheduleDrain(LiteNetLibTransport transport)
        { if (Volatile.Read(ref _disposed) == 0) _drains.Enqueue(transport); }
        private void Shutdown()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stopping.Cancel(); _connecting?.TrySetCanceled();
            lock (_pollGate)
            {
                var previous = _dispatchOwner; _dispatchOwner = this;
                try { _manager.Stop(); }
                finally
                {
                    foreach (var peer in _peers)
                        try { peer.Value.End(); } catch { /* One subscriber cannot prevent other connections/resources closing. */ }
                    _peers.Clear();
                    while (_accepted.TryDequeue(out _)) { }
                    while (_drains.TryDequeue(out _)) { }
                    _dispatchOwner = previous;
                }
            }
        }
        public ValueTask DisposeAsync() => new ValueTask(DisposeUniTaskAsync().AsTask());
        public async UniTask DisposeUniTaskAsync()
        {
            // Callback disposal must never await its own poll task. Shutdown is synchronous
            // and the canceled loop exits after this callback unwinds.
            bool dispatching = ReferenceEquals(_dispatchOwner, this);
            Shutdown();
            if (dispatching) return;
            if (_poll.HasValue) await _poll.Value;
        }
    }
}
