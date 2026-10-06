using Cysharp.Threading.Tasks;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    /// <summary>A single TCP+UDP connection: reliable framing and authenticated unreliable datagrams share admission.</summary>
    public sealed class TcpTransport : ITransport, ITransportLifetime, IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly int _maxFrameBytes;
        private readonly UniTask _receiveLoop;
        private readonly UniTask _udpLoop;
        private readonly UniTask _udpProofLoop;
        private readonly UdpClient _udp;
        private IPEndPoint _remoteUdp;
        private readonly IPAddress _remoteAddress;
        private readonly byte[] _receiveToken, _sendToken;
        private readonly byte[] _udpHello = new byte[17], _udpAck = new byte[17];
        private readonly UniTaskCompletionSource<bool> _udpReady = new();
        private readonly byte[] _sendHeader = new byte[4];
        private bool _disposed;
        private readonly object _receiveGate = new();
        private Action<ReadOnlyMemory<byte>>? _received;
        private readonly Queue<ReadOnlyMemory<byte>> _backlog = new();

        private TcpTransport(TcpClient client, UdpClient udp, IPEndPoint remoteUdp, byte[] receiveToken, byte[] sendToken, int maxFrameBytes)
        {
            _client = client;
            _stream = client.GetStream();
            _maxFrameBytes = maxFrameBytes;
            _udp = udp; _remoteUdp = remoteUdp; _receiveToken = receiveToken; _sendToken = sendToken;
            _remoteAddress = remoteUdp.Address;
            sendToken.CopyTo(_udpHello, 0); sendToken.CopyTo(_udpAck, 0); _udpHello[16] = 1; _udpAck[16] = 2;
            _receiveLoop = ReceiveLoop(_lifetime.Token).Preserve();
            _udpLoop = ReceiveUdp(_lifetime.Token).Preserve();
            _udpProofLoop = ProveUdp(_lifetime.Token).Preserve();
        }

        public event Action<ReadOnlyMemory<byte>>? OnReceived
        {
            add
            {
                ReadOnlyMemory<byte>[] backlog;
                lock (_receiveGate) { _received += value; backlog = _backlog.ToArray(); _backlog.Clear(); }
                foreach (var packet in backlog) value?.Invoke(packet);
            }
            remove { lock (_receiveGate) _received -= value; }
        }
        private void Deliver(ReadOnlyMemory<byte> payload)
        {
            Action<ReadOnlyMemory<byte>>? handler;
            lock (_receiveGate)
            {
                handler = _received;
                if (handler == null) { if (_backlog.Count >= 32) throw new InvalidDataException("Unconsumed transport backlog exceeded."); _backlog.Enqueue(payload.ToArray()); return; }
            }
            handler(payload);
        }
        public event Action<Exception>? Faulted;
        public event Action? Closed;
        public bool IsUnreliableReady => _udpReady.Task.Status == UniTaskStatus.Succeeded;

        public static async UniTask<TcpTransport> AcceptAsync(TcpClient client, int maxFrameBytes = NetRpcCodec.MaxPayloadBytes + NetRpcCodec.HeaderBytes + 6,
            CancellationToken cancellationToken = default)
        {
            if (maxFrameBytes < 1 || maxFrameBytes > NetRpcCodec.MaxPayloadBytes + NetRpcCodec.HeaderBytes + 6) throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
            client.NoDelay = true;
            var address = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var udp = new UdpClient(new IPEndPoint(address, 0));
            try
            {
                var local = new byte[18];
                BinaryPrimitives.WriteUInt16LittleEndian(local, (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port);
                using (var random = RandomNumberGenerator.Create()) { var token = new byte[16]; random.GetBytes(token); token.CopyTo(local, 2); }
                var stream = client.GetStream(); await stream.WriteAsync(local, 0, local.Length, cancellationToken).ConfigureAwait(false);
                var peer = new byte[18]; int offset = 0;
                while (offset != peer.Length)
                {
                    int count = await stream.ReadAsync(peer, offset, peer.Length - offset, cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException(); offset += count;
                }
                int port = BinaryPrimitives.ReadUInt16LittleEndian(peer); if (port == 0) throw new InvalidDataException("Invalid UDP admission.");
                return new TcpTransport(client, udp, new IPEndPoint(remote, port), local.AsSpan(2).ToArray(), peer.AsSpan(2).ToArray(), maxFrameBytes);
            }
            catch { udp.Dispose(); client.Dispose(); throw; }
        }

        public static async UniTask<TcpTransport> ConnectAsync(string host, int port,
             int maxFrameBytes = NetRpcCodec.MaxPayloadBytes + NetRpcCodec.HeaderBytes + 6, CancellationToken cancellationToken = default)
        {
            var client = new TcpClient();
            try
            {
                await WaitWithCancellation(AwaitSystemTask(client.ConnectAsync(host, port)), cancellationToken);
                return await AcceptAsync(client, maxFrameBytes, cancellationToken);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static async UniTask<TcpTransport> ListenOnceAsync(IPEndPoint endpoint,
            int maxFrameBytes = NetRpcCodec.MaxPayloadBytes + NetRpcCodec.HeaderBytes + 6, CancellationToken cancellationToken = default)
        {
            var listener = new TcpListener(endpoint);
            listener.Start();
            try
            {
                using var registration = cancellationToken.Register(listener.Stop);
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                return await AcceptAsync(client, maxFrameBytes, cancellationToken);
            }
            finally
            {
                listener.Stop();
            }
        }

        public async UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpTransport));
            if (payload.Length > _maxFrameBytes) throw new InvalidDataException("TCP NetRpc frame exceeds the configured limit.");
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(_sendHeader, payload.Length);
                await _stream.WriteAsync(_sendHeader, 0, _sendHeader.Length, cancellationToken).ConfigureAwait(false);
                if (!payload.IsEmpty)
                    await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }

        public async UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpTransport));
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length > 60000) throw new InvalidDataException("UDP payload exceeds 60000 bytes.");
            if (!IsUnreliableReady)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try { await WaitWithCancellation(_udpReady.Task.AsUniTask(), ready.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new RpcException(RpcError.Disconnected, "UDP endpoint proof is unavailable."); }
            }
            var packet = ArrayPool<byte>.Shared.Rent(payload.Length + 17);
            try
            {
                _sendToken.CopyTo(packet, 0); packet[16] = 0; payload.Span.CopyTo(packet.AsSpan(17));
#if NET8_0_OR_GREATER
                await _udp.Client.SendToAsync(packet.AsMemory(0, payload.Length + 17), SocketFlags.None, Volatile.Read(ref _remoteUdp), cancellationToken).ConfigureAwait(false);
#else
                await _udp.SendAsync(packet, payload.Length + 17, Volatile.Read(ref _remoteUdp)).ConfigureAwait(false);
#endif
            }
            finally { ArrayPool<byte>.Shared.Return(packet); }
        }
        private async UniTask ProveUdp(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !IsUnreliableReady)
                {
                    await _udp.SendAsync(_udpHello, _udpHello.Length, Volatile.Read(ref _remoteUdp)).ConfigureAwait(false);
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { Faulted?.Invoke(error); }
        }
        private async UniTask ReceiveUdp(CancellationToken cancellationToken)
        {
            // A datagram is borrowed only until Deliver returns. Keep one receive buffer for the connection.
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            EndPoint template = new IPEndPoint(_remoteAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
#if NET8_0_OR_GREATER
            var address = template.Serialize();
            var knownAddress = Volatile.Read(ref _remoteUdp).Serialize();
#else
            using var receiver = new NetRpcDatagramReceiver(_udp.Client, buffer, template);
#endif
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
#if NET8_0_OR_GREATER
                    var count = await _udp.Client.ReceiveFromAsync(buffer.AsMemory(0, 65536), SocketFlags.None, address, cancellationToken).ConfigureAwait(false);
                    IPEndPoint endpoint;
                    if (address.Equals(knownAddress)) endpoint = Volatile.Read(ref _remoteUdp);
                    else
                    {
                        endpoint = (IPEndPoint)template.Create(address);
                        if (!endpoint.Address.Equals(_remoteAddress)) continue;
                    }
#else
                    var received = await receiver.Receive();
                    var count = received.ReceivedBytes;
                    var endpoint = (IPEndPoint)received.RemoteEndPoint;
                    if (!endpoint.Address.Equals(_remoteAddress)) continue;
#endif
                    if (count < 17 || count > 60017 || !buffer.AsSpan(0, 16).SequenceEqual(_receiveToken)) continue;
                    // Authenticated UDP proof learns the actual NAT-mapped port, never the unauthenticated advertised port alone.
                    Volatile.Write(ref _remoteUdp, endpoint);
#if NET8_0_OR_GREATER
                    if (!address.Equals(knownAddress)) knownAddress = endpoint.Serialize();
#endif
                    if (count == 17 && buffer[16] == 1)
                    { await _udp.SendAsync(_udpAck, _udpAck.Length, endpoint).ConfigureAwait(false); continue; }
                    if (count == 17 && buffer[16] == 2)
                    { _udpReady.TrySetResult(true); continue; }
                    if (buffer[16] != 0) continue;
                    Deliver(buffer.AsMemory(17, count - 17));
                }
            }
            catch (Exception error) when (cancellationToken.IsCancellationRequested) { _ = error; }
            catch (Exception error) { Faulted?.Invoke(error); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private async UniTask ReceiveLoop(CancellationToken cancellationToken)
        {
            var header = new byte[4];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int offset = 0;
                    while (offset < header.Length)
                    {
                        var read = await _stream.ReadAsync(header.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException();
                        offset += read;
                    }
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > _maxFrameBytes)
                        throw new InvalidDataException("Invalid TCP NetRpc frame length.");
                    var payload = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                    try
                    {
                        offset = 0;
                        while (offset < length)
                        {
                            var read = await _stream.ReadAsync(payload.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException();
                            offset += read;
                        }
                        Deliver(payload.AsMemory(0, length));
                    }
                    finally { ArrayPool<byte>.Shared.Return(payload); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (Exception error) when (cancellationToken.IsCancellationRequested) { _ = error; }
            catch (Exception error) { Faulted?.Invoke(error); }
            finally { Closed?.Invoke(); }
        }

        private static async UniTask AwaitSystemTask(Task task) => await task.ConfigureAwait(false);
        private static UniTask WaitWithCancellation(UniTask task, CancellationToken cancellationToken) => task.AttachExternalCancellation(cancellationToken);

        public ValueTask DisposeAsync() => new ValueTask(DisposeUniTaskAsync().AsTask());
        public async UniTask DisposeUniTaskAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _udpReady.TrySetCanceled();
            _client.Close();
            _udp.Dispose();
            try { await _receiveLoop; } catch { }
            try { await _udpLoop; } catch { }
            try { await _udpProofLoop; } catch { }
            _sendGate.Dispose();
            _lifetime.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public sealed class TcpTransportListener : IDisposable
    {
        private readonly TcpListener _listener;
        public TcpTransportListener(IPEndPoint endpoint) { _listener = new TcpListener(endpoint); _listener.Start(); }
        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
        public UniTask<TcpTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
            AcceptAsync(Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>Bounds only the accepted connection's TCP/UDP proof; a bad peer cannot stop the listener.</summary>
        public async UniTask<TcpTransport> AcceptAsync(TimeSpan handshakeTimeout,
            CancellationToken cancellationToken = default)
        {
            if (handshakeTimeout != Timeout.InfiniteTimeSpan &&
                (handshakeTimeout <= TimeSpan.Zero || handshakeTimeout > TimeSpan.FromMinutes(1)))
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
            using var registration = cancellationToken.Register(_listener.Stop);
            var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (handshakeTimeout != Timeout.InfiniteTimeSpan) handshake.CancelAfter(handshakeTimeout);
            return await TcpTransport.AcceptAsync(client, cancellationToken: handshake.Token);
        }
        public void Dispose() => _listener.Stop();
    }
}
