using Cysharp.Threading.Tasks;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    public interface IRelayEndpoint : IAsyncDisposable { IPEndPoint EndPoint { get; } }

    internal static class RelayFrame
    {
        public const byte Magic = 0xFA, Register = 1, Accepted = 2, Joined = 3, Left = 4, Reliable = 5, Fast = 6;
        public static async UniTask Send(TcpTransport transport, byte kind, uint peer, ReadOnlyMemory<byte> payload = default, bool fast = false, CancellationToken cancellationToken = default)
        {
            var bytes = ArrayPool<byte>.Shared.Rent(6 + payload.Length);
            try
            {
                bytes[0] = Magic; bytes[1] = kind; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), peer); payload.Span.CopyTo(bytes.AsSpan(6));
                if (fast) await transport.SendFast(bytes.AsMemory(0, 6 + payload.Length), cancellationToken);
                else await transport.Send(bytes.AsMemory(0, 6 + payload.Length), cancellationToken);
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
        }
        public static bool TryRead(ReadOnlyMemory<byte> data, out byte kind, out uint peer, out ReadOnlyMemory<byte> payload)
        {
            kind = 0; peer = 0; payload = default;
            if (data.Length < 6 || data.Span[0] != Magic) return false;
            kind = data.Span[1]; peer = BinaryPrimitives.ReadUInt32LittleEndian(data.Span.Slice(2)); payload = data.Slice(6); return true;
        }
    }

    /// <summary>Transparent endpoint: ordinary clients connect with exactly the same TcpTransport API as Direct.</summary>
    public sealed class RelayEndpoint : IRelayEndpoint
    {
        private readonly TcpTransportListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentDictionary<uint, TcpTransport> _clients = new();
        private readonly ConcurrentDictionary<TcpTransport, byte> _accepted = new();
        private readonly UniTask _accept;
        private readonly string _hostKey;
        private readonly object _gate = new();
        private TcpTransport? _host;
        private int _peer = 1000;
        private int _disposed;
        public RelayEndpoint(IPEndPoint endpoint, string hostKey)
        {
            if (string.IsNullOrEmpty(hostKey) || hostKey.Length > 256) throw new ArgumentException("Relay host key must be 1..256 characters.");
            _hostKey = hostKey; _listener = new TcpTransportListener(endpoint); _accept = AcceptLoop();
        }
        public IPEndPoint EndPoint => _listener.EndPoint;
        public event Action<Exception>? Faulted;
        private async UniTask AcceptLoop()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var connection = await _listener.AcceptAsync(_lifetime.Token); _accepted.TryAdd(connection, 0);
                    uint assigned = unchecked((uint)Interlocked.Increment(ref _peer));
                    bool registered = false;
                    var serial = new SemaphoreSlim(1, 1);
                    connection.OnReceived += data =>
                    {
                        var owned = ArrayPool<byte>.Shared.Rent(data.Length); data.Span.CopyTo(owned);
                        Handle(connection, assigned, owned, data.Length, serial, () => registered, () => registered = true).Forget();
                    };
                    connection.Closed += () => Disconnected(connection, assigned);
                }
                catch (Exception) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error) { Faulted?.Invoke(error); }
            }
        }
        private async UniTaskVoid Handle(TcpTransport connection, uint assigned, byte[] owned, int length, SemaphoreSlim serial, Func<bool> registered, Action register)
        {
            var bytes = owned.AsMemory(0, length);
            try
            {
                await serial.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (RelayFrame.TryRead(bytes, out var kind, out var destination, out var payload))
                    {
                        if (kind == RelayFrame.Register)
                        {
                            if (registered()) throw new InvalidOperationException("Relay role cannot change.");
                            var reader = new NetMessageReader(payload, 1); var key = reader.Read<string>(); reader.Complete();
                            if (key != _hostKey) throw new RpcException(RpcError.Unauthorized, "Relay Host authentication failed.");
                            lock (_gate) { if (_host != null) throw new InvalidOperationException("Relay already has an authority Host."); _host = connection; register(); }
                            await RelayFrame.Send(connection, RelayFrame.Accepted, 1); return;
                        }
                        if (!ReferenceEquals(connection, _host) || kind != RelayFrame.Reliable && kind != RelayFrame.Fast)
                            throw new RpcException(RpcError.Unauthorized, "Only the admitted Host may route relay frames.");
                        if (_clients.TryGetValue(destination, out var client))
                        {
                            if (kind == RelayFrame.Fast) await client.SendFast(payload);
                            else await client.Send(payload);
                        }
                        return;
                    }
                    if (ReferenceEquals(connection, _host)) throw new InvalidOperationException("Host relay frames require destination metadata.");
                    var host = _host ?? throw new RpcException(RpcError.Disconnected, "Relay Host unavailable.");
                    if (_clients.TryAdd(assigned, connection))
                    { register(); await RelayFrame.Send(host, RelayFrame.Joined, assigned); }
                    var rpc = NetRpcCodec.Decode(bytes);
                    bool fast = rpc.Kind == NetRpcMessageKind.Component || rpc.Kind == NetRpcMessageKind.FastCall;
                    await RelayFrame.Send(host, fast ? RelayFrame.Fast : RelayFrame.Reliable, assigned, bytes, fast);
                }
                finally { serial.Release(); }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { Faulted?.Invoke(error); await connection.DisposeUniTaskAsync(); }
            finally { ArrayPool<byte>.Shared.Return(owned); }
        }
        private void Disconnected(TcpTransport connection, uint assigned)
        {
            _accepted.TryRemove(connection, out _);
            if (ReferenceEquals(connection, _host))
            {
                lock (_gate) if (ReferenceEquals(connection, _host)) _host = null;
                foreach (var client in _clients.Values) Observe(client.DisposeUniTaskAsync()).Forget(); _clients.Clear();
            }
            else if (_clients.TryRemove(assigned, out _) && _host != null)
                Observe(RelayFrame.Send(_host, RelayFrame.Left, assigned)).Forget();
        }
        private async UniTaskVoid Observe(UniTask task) { try { await task; } catch (Exception error) { Faulted?.Invoke(error); } }
        public ValueTask DisposeAsync() => new ValueTask(DisposeUniTaskAsync().AsTask());
        public async UniTask DisposeUniTaskAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel(); _listener.Dispose();
            await _accept;
            foreach (var connection in _accepted.Keys) await connection.DisposeUniTaskAsync();
            _accepted.Clear(); _clients.Clear(); _lifetime.Dispose();
        }
    }

    /// <summary>Optional Host sidecar. Direct peers continue independently while this connector retries Relay admission.</summary>
    public sealed class RelayHostConnection : IAsyncDisposable
    {
        private sealed class PeerTransport : ITransport
        {
            private readonly TcpTransport _link;
            private readonly uint _peer;
            public PeerTransport(TcpTransport link, uint peer) { _link = link; _peer = peer; }
            public event Action<ReadOnlyMemory<byte>>? OnReceived;
            public void Deliver(ReadOnlyMemory<byte> data) => OnReceived?.Invoke(data);
            public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
                RelayFrame.Send(_link, RelayFrame.Reliable, _peer, payload, cancellationToken: cancellationToken);
            public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
                RelayFrame.Send(_link, RelayFrame.Fast, _peer, payload, true, cancellationToken);
        }
        private readonly RpcContextService _runtime;
        private readonly string _host, _key;
        private readonly int _port;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentDictionary<uint, PeerTransport> _peers = new();
        private readonly UniTask _loop;
        private TcpTransport? _link;
        private int _disposed;
        public RelayHostConnection(RpcContextService runtime, string host, int port, string hostKey)
        {
            if (!runtime.IsServer) throw new RpcException(RpcError.InvalidRole, "Relay sidecar requires Host.");
            _runtime = runtime; _host = host; _port = port; _key = hostKey; _loop = Run();
        }
        public bool IsConnected { get; private set; }
        public event Action<Exception>? ConnectionFailed;
        private async UniTask Run()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); attempt.CancelAfter(TimeSpan.FromSeconds(5));
                    var link = await TcpTransport.ConnectAsync(_host, _port, cancellationToken: attempt.Token); _link = link;
                    var accepted = new UniTaskCompletionSource<bool>();
                    var closed = new UniTaskCompletionSource<bool>();
                    link.Closed += () => closed.TrySetResult(true);
                    link.OnReceived += data =>
                    {
                        try
                        {
                            if (!RelayFrame.TryRead(data, out var kind, out var peer, out var payload)) return;
                            if (kind == RelayFrame.Accepted) { accepted.TrySetResult(true); return; }
                            if (kind == RelayFrame.Joined)
                            { var transport = new PeerTransport(link, peer); if (_peers.TryAdd(peer, transport)) _runtime.AttachPeer(peer, transport); return; }
                            if (kind == RelayFrame.Left) { _peers.TryRemove(peer, out _); _runtime.DetachPeer(peer); return; }
                            if (_peers.TryGetValue(peer, out var receiver)) receiver.Deliver(payload);
                        }
                        catch (Exception error) { ConnectionFailed?.Invoke(error); }
                    };
                    using var bag = NetMessageBag.Pool(); bag.Write(_key); await RelayFrame.Send(link, RelayFrame.Register, 1, bag.Memory, cancellationToken: attempt.Token);
                    using var admissionCancellation = attempt.Token.Register(() => accepted.TrySetCanceled());
                    await accepted.Task; IsConnected = true;
                    using var shutdown = _lifetime.Token.Register(() => closed.TrySetResult(true)); await closed.Task;
                }
                catch (Exception) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error) { ConnectionFailed?.Invoke(error); }
                finally
                {
                    IsConnected = false; foreach (var peer in _peers.Keys) _runtime.DetachPeer(peer); _peers.Clear();
                    if (_link != null) { await _link.DisposeUniTaskAsync(); _link = null; }
                }
                try { await Task.Delay(300, _lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        public ValueTask DisposeAsync() => new ValueTask(DisposeUniTaskAsync().AsTask());
        public async UniTask DisposeUniTaskAsync() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _lifetime.Cancel(); await _loop; _lifetime.Dispose(); }
    }
}
