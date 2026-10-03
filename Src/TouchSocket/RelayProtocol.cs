using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Dmtp;
using TouchSocket.Sockets;

[assembly: InternalsVisibleTo("BITKit.Multiplayer.RelayTests")]

namespace BITKit.Multiplayer.TouchSocket
{
    // One reserved DMTP protocol. Byte zero of its payload is the relay operation;
    // the remaining bytes are bounded controls or opaque runtime data.
    internal enum RelayOp : byte
    {
        Register = 1, Select = 2, Activate = 3, Ack = 4, Admit = 5,
        AdmissionRequest = 6, AdmissionResult = 7, AdmissionReply = 8,
        Data = 9, Prepared = 10, DirectoryReady = 11, Leave = 12,
        Retire = 13, Retired = 14, Left = 15, UdpGrant = 16, UdpRotate = 17
    }

    internal static class RelayProtocol
    {
        internal const ushort Protocol = 220;
        internal const int TicketLimit = 4096;
        internal const int AbsoluteLimit = 16 * 1024 * 1024;
        internal static byte[] Pack(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            { write(writer); return stream.ToArray(); }
        }
        internal static T Parse<T>(byte[] bytes, Func<BinaryReader, T> read)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var result = read(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing relay data");
                return result;
            }
        }
        // Controls remain cold BinaryReader/MemoryStream messages. Gameplay never enters this adapter.
        internal static T Parse<T>(ReadOnlyMemory<byte> bytes, Func<BinaryReader, T> read) => Parse(bytes.ToArray(), read);
        internal static (PeerId? Peer, ReadOnlyMemory<byte> Bytes) ParseData(ReadOnlyMemory<byte> frame, bool withPeer, int max)
        {
            var span = frame.Span;
            int offset = 0;
            PeerId? peer = null;
            if (withPeer)
            {
                int textLength = 0, shift = 0;
                for (int i = 0; i < 5; i++)
                {
                    if (offset >= span.Length || i == 4 && span[offset] > 0x0f) throw new InvalidDataException("Invalid relay peer length");
                    byte part = span[offset++]; textLength |= (part & 0x7f) << shift;
                    if ((part & 0x80) == 0) break;
                    if (i == 4) throw new InvalidDataException("Invalid relay peer length");
                    shift += 7;
                }
                if (textLength < 1 || textLength > 128 || span.Length - offset < textLength) throw new InvalidDataException("Invalid relay peer");
                peer = new PeerId(Encoding.UTF8.GetString(span.Slice(offset, textLength)));
                offset += textLength;
            }
            if (span.Length - offset < 4) throw new InvalidDataException("Missing relay data length");
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4)); offset += 4;
            if (size < 0 || size > max || size != span.Length - offset) throw new InvalidDataException("Invalid relay data size");
            return (peer, frame.Slice(offset, size)); // borrowed from one owned incoming frame
        }
        internal static int DataHeaderSize(string? peer)
        {
            if (peer == null) return 1 + 4;
            int length = Encoding.UTF8.GetByteCount(peer);
            if (length is < 1 or > 128) throw new ArgumentException("Invalid relay peer", nameof(peer));
            int count = length, prefix = 1;
            while ((count >>= 7) != 0) prefix++;
            return 1 + prefix + length + 4;
        }
        internal static int WriteDataHeader(Span<byte> destination, string? peer, int size)
        {
            int index = 0;
            destination[index++] = (byte)RelayOp.Data;
            if (peer != null)
            {
                int length = Encoding.UTF8.GetByteCount(peer), count = length;
                while (count >= 128) { destination[index++] = (byte)((count & 0x7f) | 0x80); count >>= 7; }
                destination[index++] = (byte)count;
                index += Encoding.UTF8.GetBytes(peer.AsSpan(), destination.Slice(index, length));
            }
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(index, 4), size);
            return index + 4;
        }
        internal static string Text(BinaryReader reader, int max = TicketLimit)
        {
            var text = reader.ReadString();
            if (Encoding.UTF8.GetByteCount(text) > max) throw new InvalidDataException("Relay text exceeds limit");
            return text;
        }
        internal static byte[] Blob(BinaryReader reader, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max) throw new InvalidDataException("Relay payload exceeds limit");
            var result = reader.ReadBytes(count);
            if (result.Length != count) throw new EndOfStreamException();
            return result;
        }
        internal static void Blob(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        internal static void CheckOptions(RelayConnectOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.Address) || options.Port < 1 || options.Port > 65535 ||
                string.IsNullOrWhiteSpace(options.VerifyToken) || string.IsNullOrWhiteSpace(options.RoomId) ||
                string.IsNullOrWhiteSpace(options.Scope) || string.IsNullOrWhiteSpace(options.HostPeerId.Value) ||
                Encoding.UTF8.GetByteCount(options.RoomId) > 256 || Encoding.UTF8.GetByteCount(options.Scope) > 256 ||
                Encoding.UTF8.GetByteCount(options.VerifyToken) > 256)
                throw new ArgumentException("Invalid relay connection options", nameof(options));
            if (options.UseTls && string.IsNullOrWhiteSpace(options.TlsTargetHost))
                throw new ArgumentException("TLS target host is required", nameof(options));
        }
    }

    // Bounded ordered DMTP writer and incoming mailbox. Each message owns ONE ArrayPool
    // rental; borrowed plugin receive memory is copied exactly once before callback return.
    internal sealed class RelaySocket : IDisposable
    {
        private readonly Func<ReadOnlyMemory<byte>, Task> _send;
        private readonly Action _close;
        private readonly int _maxMessage, _maxQueue, _maxBytes;
        private readonly Queue<Outgoing> _outgoing = new Queue<Outgoing>();
        private readonly Queue<IncomingFrame> _incoming = new Queue<IncomingFrame>();
        private readonly SemaphoreSlim _writeSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _readSignal = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private readonly Task _writer;
        private long _outBytes, _inBytes;
        private int _outCount, _outstanding;
        private long _rents, _returns;
        private bool _closed;
        internal int PoolOutstanding => Volatile.Read(ref _outstanding);
        internal long PoolRents => Interlocked.Read(ref _rents);
        internal long PoolReturns => Interlocked.Read(ref _returns);
        internal Task Completion => _writer;
        private byte[] Rent(int length)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
            Interlocked.Increment(ref _rents); Interlocked.Increment(ref _outstanding);
            return buffer;
        }
        private void Return(byte[] buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Increment(ref _returns); Interlocked.Decrement(ref _outstanding);
        }
        internal sealed class IncomingFrame : IDisposable
        {
            private RelaySocket? _owner;
            internal readonly byte[] Buffer;
            internal readonly int Length;
            internal RelayOp Op => (RelayOp)Buffer[0];
            internal ReadOnlyMemory<byte> Payload => Buffer.AsMemory(1, Length - 1);
            internal IncomingFrame(RelaySocket owner, byte[] buffer, int length) { _owner = owner; Buffer = buffer; Length = length; }
            public void Dispose() { var owner = Interlocked.Exchange(ref _owner, null); if (owner != null) owner.Return(Buffer); }
        }
        private sealed class Outgoing
        {
            internal byte[] Buffer = Array.Empty<byte>();
            internal int Length;
            internal TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal RelaySocket(Func<ReadOnlyMemory<byte>, Task> send, Action close, int maxMessage, int maxQueue, int maxBytes)
        {
            _send = send; _close = close; _maxMessage = maxMessage; _maxQueue = maxQueue; _maxBytes = maxBytes;
            _writer = WriteLoop(); // one ordered writer per physical session
        }
        internal static async Task<RelaySocket> Dial(RelayConnectOptions options, CancellationToken token)
        {
            var client = new TcpDmtpClient();
            RelaySocket? socket = null;
            try
            {
                var config = new TouchSocketConfig()
                    .ConfigurePlugins(plugins =>
                    {
                        plugins.AddDmtpReceivedPlugin((IDmtpActorObject actor, DmtpMessageEventArgs e) =>
                        {
                            if (ReferenceEquals(actor, client) && e.DmtpMessage.ProtocolFlags == RelayProtocol.Protocol)
                            {
                                if (e.DmtpMessage.Memory.Length < 1 || e.DmtpMessage.Memory.Length > RelayProtocol.AbsoluteLimit + 1)
                                    socket?.Dispose();
                                else socket?.Enqueue(e.DmtpMessage.Memory);
                            }
                            return Task.CompletedTask;
                        });
                        plugins.AddDmtpClosedPlugin((IDmtpActorObject actor) => { if (ReferenceEquals(actor, client)) socket?.Dispose(); });
                        plugins.AddTcpClosedPlugin((ITcpSession actor) => { if (ReferenceEquals(actor, client)) socket?.Dispose(); });
                    })
                    .SetDmtpOption(o => o.VerifyToken = options.VerifyToken)
                    .SetRemoteIPHost(options.Address + ":" + options.Port);
                if (options.UseTls) config.SetClientSslOption(o =>
                {
                    o.TargetHost = options.TlsTargetHost!;
                    // TouchSocket 4.3.9's default callback ACCEPTS ALL certificates.
                    // Require both a trusted chain and a matching TLS target hostname.
                    o.CertificateValidationCallback = (_, __, ___, errors) => errors == SslPolicyErrors.None;
                });
                await client.SetupAsync(config).ConfigureAwait(false);
                async Task CloseClient()
                {
                    try
                    {
                        if (client.Online)
                        {
                            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            {
                                var close = client.CloseAsync("Relay wire closed", timeout.Token);
                                if (await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) == close)
                                    await close.ConfigureAwait(false);
                                else _ = close.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            }
                        }
                    }
                    catch { }
                    finally { client.Dispose(); }
                }
                // At most one bounded close operation per physical socket; send DMTP close
                // before disposing TCP so the server can retire its routing immediately.
                socket = new RelaySocket(bytes => client.DmtpActor.SendAsync(RelayProtocol.Protocol, bytes),
                    () => { _ = CloseClient(); }, RelayProtocol.AbsoluteLimit, 128, 4 * 1024 * 1024);
                using (token.Register(() => client.Dispose())) await client.ConnectAsync().ConfigureAwait(false);
                return socket;
            }
            catch { socket?.Dispose(); client.Dispose(); throw; }
        }
        // One owned rental before the TouchSocket plugin returns. No second body copy.
        internal void Enqueue(ReadOnlyMemory<byte> frame)
        {
            if (frame.Length < 1 || frame.Length - 1 > _maxMessage || frame.Length > RelayProtocol.AbsoluteLimit + 1)
            { Dispose(); return; }
            bool overflow;
            lock (_gate)
            {
                if (_closed) return;
                overflow = _incoming.Count >= _maxQueue || _inBytes + frame.Length > _maxBytes;
                if (!overflow)
                {
                    var owned = Rent(frame.Length);
                    try { frame.CopyTo(owned.AsMemory(0, frame.Length)); }
                    catch { Return(owned); throw; }
                    _incoming.Enqueue(new IncomingFrame(this, owned, frame.Length)); _inBytes += frame.Length;
                    _readSignal.Release();
                }
            }
            if (overflow) Dispose();
        }
        internal Task Send(RelayOp op, ReadOnlyMemory<byte> payload) => EnqueueSend(op, payload, null, false);
        internal Task SendData(ReadOnlyMemory<byte> payload, string? peer = null) => EnqueueSend(RelayOp.Data, payload, peer, true);
        private Task EnqueueSend(RelayOp op, ReadOnlyMemory<byte> payload, string? dataPeer, bool encodeData)
        {
            if (payload.Length > _maxMessage || payload.Length > RelayProtocol.AbsoluteLimit)
                throw new RpcException(RpcError.LimitExceeded, "Relay message exceeds limit");
            int header = encodeData ? RelayProtocol.DataHeaderSize(dataPeer) : 1;
            // Cold Send(Data, already-packed legacy payload) remains available to tests.
            int length = checked(header + payload.Length);
            if (length - 1 > _maxMessage || length > RelayProtocol.AbsoluteLimit + 1)
                throw new RpcException(RpcError.LimitExceeded, "Relay message exceeds limit");
            var item = new Outgoing { Length = length };
            bool overflow;
            lock (_gate)
            {
                if (_closed) throw new RpcException(RpcError.Disconnected, "Relay socket closed");
                overflow = _outCount >= _maxQueue || _outBytes + length > _maxBytes;
                if (!overflow)
                {
                    item.Buffer = Rent(length);
                    try
                    {
                        if (encodeData)
                            RelayProtocol.WriteDataHeader(item.Buffer.AsSpan(0, header), dataPeer, payload.Length);
                        else item.Buffer[0] = (byte)op;
                        payload.CopyTo(item.Buffer.AsMemory(header, payload.Length));
                    }
                    catch { Return(item.Buffer); throw; }
                    _outgoing.Enqueue(item); _outBytes += length; _outCount++; _writeSignal.Release();
                }
            }
            if (overflow) { Dispose(); throw new RpcException(RpcError.LimitExceeded, "Relay send queue full"); }
            return item.Done.Task;
        }
        internal async Task<IncomingFrame> Read(CancellationToken token)
        {
            await _readSignal.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_incoming.Count != 0)
                {
                    var frame = _incoming.Dequeue(); _inBytes -= frame.Length;
                    return frame;
                }
                throw new RpcException(RpcError.Disconnected, "Relay socket closed");
            }
        }
        private async Task WriteLoop()
        {
            try
            {
                while (true)
                {
                    await _writeSignal.WaitAsync().ConfigureAwait(false);
                    Outgoing item;
                    lock (_gate)
                    {
                        if (_closed) return;
                        item = _outgoing.Dequeue();
                    }
                    try
                    {
                        // Do not release the pooled wire frame or complete its owner's task
                        // until the real underlying DMTP send has finished using it.
                        await _send(item.Buffer.AsMemory(0, item.Length)).ConfigureAwait(false);
                        lock (_gate) if (_closed) throw new RpcException(RpcError.Disconnected, "Relay socket closed");
                        item.Done.TrySetResult(true);
                    }
                    catch { item.Done.TrySetException(new RpcException(RpcError.Disconnected, "Relay write failed")); throw; }
                    finally { lock (_gate) { _outBytes -= item.Length; _outCount--; } Return(item.Buffer); }
                }
            }
            catch { Dispose(); }
        }
        public void Dispose()
        {
            Queue<Outgoing> pending;
            Queue<IncomingFrame> incoming;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = new Queue<Outgoing>(_outgoing); _outgoing.Clear();
                incoming = new Queue<IncomingFrame>(_incoming); _incoming.Clear();
                foreach (var item in pending) { _outBytes -= item.Length; _outCount--; }
                _inBytes = 0;
                _readSignal.Release(); _writeSignal.Release();
            }
            foreach (var item in pending)
            { item.Done.TrySetException(new RpcException(RpcError.Disconnected, "Relay socket closed")); Return(item.Buffer); }
            foreach (var frame in incoming) frame.Dispose();
            try { _close(); } catch { }
        }
    }
}
