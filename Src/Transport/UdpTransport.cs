using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BITKit.Multiplayer.Transport
{
    /// <summary>A single-use IPv4 UDP endpoint. Receive memory is valid only inside Received.</summary>
    public sealed class UdpTransport : ITransport
    {
        private const int ReceiveCapacity = 65536;
        private const int MaxIpv4Payload = 65507;
        private const int SynchronousReceiveBatch = 64;
        private const int SioUdpConnReset = unchecked((int)0x9800000C);
        private static readonly WaitCallback ContinueReceive = state => ((UdpTransport)state!).PumpReceive();
        private readonly object gate = new object();
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stack<SocketAsyncEventArgs> idleSends = new Stack<SocketAsyncEventArgs>();
        private readonly IPEndPoint receiveTemplate = new IPEndPoint(IPAddress.Any, 0);
        private Socket? socket;
        private SocketAsyncEventArgs? receiveArgs;
        private byte[]? receiveBuffer;
        private EndPoint? localEndPoint;
        private bool started;
        private bool stopping;
        private bool receiving;
        private int maxPacketBytes;
        private int maxConcurrentSends;
        private int inFlight;
        private long sent, received, rejected, rents, returns, outstanding;

        public TransportCapabilities Capabilities => TransportCapabilities.Unreliable;
        public EndPoint? LocalEndPoint { get { lock (gate) return localEndPoint; } }
        public Task Completion => completion.Task;
        public event Action<EndPoint, ReadOnlyMemory<byte>, RpcDelivery>? Received;
        public event Action<Exception>? Faulted;

        public Task StartAsync(TransportOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            lock (gate)
            {
                if (started || stopping) throw new InvalidOperationException("UDP endpoints are single-use.");
                started = true;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.LocalEndPoint == null || options.LocalEndPoint.AddressFamily != AddressFamily.InterNetwork)
                        throw new NotSupportedException("UdpTransport supports IPv4 endpoints only.");
                    if (options.MaxPacketBytes < 1 || options.MaxPacketBytes > MaxIpv4Payload || options.MaxConcurrentSends < 1)
                        throw new ArgumentOutOfRangeException(nameof(options), "Invalid packet size or send concurrency limit.");
                    maxPacketBytes = options.MaxPacketBytes;
                    maxConcurrentSends = options.MaxConcurrentSends;
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket = s;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Winsock otherwise turns an ICMP Port Unreachable from one departed peer
                        // into WSAECONNRESET on this shared endpoint's next receive.
                        try { s.IOControl(SioUdpConnReset, new byte[4], null); }
                        catch (SocketException) { /* Some providers reject this IOCTL; handle ICMP per receive below. */ }
                        catch (NotSupportedException) { /* Likewise for nonstandard socket providers. */ }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    s.Bind(options.LocalEndPoint);
                    cancellationToken.ThrowIfCancellationRequested();
                    localEndPoint = s.LocalEndPoint;
                    receiveBuffer = ArrayPool<byte>.Shared.Rent(ReceiveCapacity);
                    Interlocked.Increment(ref rents);
                    Interlocked.Add(ref outstanding, receiveBuffer.Length);
                    var args = new SocketAsyncEventArgs { RemoteEndPoint = receiveTemplate };
                    args.SetBuffer(receiveBuffer, 0, ReceiveCapacity);
                    args.Completed += OnReceiveCompleted;
                    receiveArgs = args;
                    receiving = true;
                }
                catch
                {
                    stopping = true;
                    socket?.Dispose();
                    socket = null;
                    ReleaseReceive();
                    completion.TrySetResult(true);
                    throw;
                }
            }
            PumpReceive();
            return Task.CompletedTask;
        }

        private void PumpReceive()
        {
            // A synchronous completion is handled in this loop, not by recursive callbacks.
            for (int completed = 0; ; completed++)
            {
                if (completed == SynchronousReceiveBatch)
                {
                    // One continuation per bounded batch, never one work item per packet.
                    // receiving remains true: StopAsync must not return the buffer yet.
                    ThreadPool.QueueUserWorkItem(ContinueReceive, this);
                    return;
                }
                Socket s;
                SocketAsyncEventArgs args;
                lock (gate)
                {
                    if (stopping) { FinishReceive(); return; }
                    s = socket!;
                    args = receiveArgs!;
                    // ReceiveFromAsync replaces RemoteEndPoint with a source endpoint. Keep
                    // the private wildcard template rather than allocating one per post.
                    args.RemoteEndPoint = receiveTemplate;
                }
                bool pending;
                try { pending = s.ReceiveFromAsync(args); }
                catch (SocketException ex) when (IsPeerUnreachable(ex.SocketErrorCode))
                {
                    // An ICMP response concerns a peer, not the listening socket.
                    continue;
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                {
                    FailReceive(ex);
                    return;
                }
                if (pending) return;
                if (!ProcessReceive(args)) return;
            }
        }

        private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs args)
        {
            if (ProcessReceive(args)) PumpReceive();
        }

        private bool ProcessReceive(SocketAsyncEventArgs args)
        {
            lock (gate) if (stopping) { FinishReceive(); return false; }
            if (args.SocketError == SocketError.Success)
            {
                if (args.BytesTransferred > maxPacketBytes)
                    Interlocked.Increment(ref rejected);
                else
                {
                    Interlocked.Increment(ref received);
                    try { Received?.Invoke(args.RemoteEndPoint!, new ReadOnlyMemory<byte>(receiveBuffer!, 0, args.BytesTransferred), RpcDelivery.Unreliable); }
                    catch (Exception ex) { ReportFault(ex); }
                }
            }
            else if (args.SocketError == SocketError.MessageSize)
                Interlocked.Increment(ref rejected);
            else if (IsPeerUnreachable(args.SocketError))
            {
                // On platforms/providers that still surface ICMP despite the Windows IOCTL.
            }
            else
            {
                FailReceive(new SocketException((int)args.SocketError));
                return false;
            }
            lock (gate) if (stopping) { FinishReceive(); return false; }
            return true;
        }

        private static bool IsPeerUnreachable(SocketError error) =>
            error == SocketError.ConnectionReset || error == SocketError.ConnectionRefused;

        private void FailReceive(Exception ex)
        {
            bool report;
            lock (gate) report = !stopping;
            SignalStop();
            FinishReceive();
            if (report) ReportFault(ex);
        }

        private void ReportFault(Exception ex)
        {
            try { Faulted?.Invoke(ex); } catch { /* A user's fault handler cannot break resource cleanup. */ }
        }

        public ValueTask SendAsync(EndPoint destination, ReadOnlyMemory<byte> payload,
            RpcDelivery delivery = RpcDelivery.Unreliable, CancellationToken cancellationToken = default)
        {
            if (delivery != RpcDelivery.Unreliable) throw new NotSupportedException("UDP does not provide reliable delivery.");
            if (destination is not IPEndPoint endpoint || endpoint.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("UdpTransport supports IPv4 destinations only.");
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!started || stopping || socket == null || !receiving)
                    throw new InvalidOperationException("UDP endpoint is not running.");
                if (payload.Length > maxPacketBytes)
                {
                    Interlocked.Increment(ref rejected);
                    throw new ArgumentOutOfRangeException(nameof(payload), "Packet exceeds MaxPacketBytes.");
                }
                if (inFlight == maxConcurrentSends)
                {
                    Interlocked.Increment(ref rejected);
                    throw new InvalidOperationException("UDP send concurrency limit reached.");
                }
                var args = idleSends.Count != 0 ? idleSends.Pop() : CreateSendArgs();
                var op = (SendOperation)args.UserToken!;
                op.CancellationToken = cancellationToken;
                inFlight++;
                bool pending;
                try
                {
                    // Reserve before submitting I/O: even allocation failure must not
                    // abandon caller memory already handed to the OS. Synchronous sends
                    // retain this unused promise; a genuinely pending send consumes it.
                    op.Result ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment))
                        args.SetBuffer(segment.Array!, segment.Offset, segment.Count);
                    else
                    {
                        op.Rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
                        Interlocked.Increment(ref rents);
                        Interlocked.Add(ref outstanding, op.Rented.Length);
                        payload.Span.CopyTo(op.Rented);
                        args.SetBuffer(op.Rented, 0, payload.Length);
                    }
                    args.RemoteEndPoint = endpoint;
                    // The gate spans only submission, never an I/O wait. A racing callback
                    // cannot finish/recycle the operation before its Task has been published.
                    pending = socket.SendToAsync(args);
                }
                catch (Exception ex)
                {
                    // Includes a disposed/custom MemoryManager throwing while materializing
                    // non-array memory: its rental and accepted slot must still be released.
                    return FinishSend(args, ex);
                }
                if (pending)
                {
                    op.Pending = true;
                    return new ValueTask(op.Result.Task);
                }
                return FinishSend(args); // synchronous success needs no operation/Task allocation
            }
        }

        private SocketAsyncEventArgs CreateSendArgs()
        {
            var args = new SocketAsyncEventArgs { UserToken = new SendOperation() };
            args.Completed += OnSendCompleted;
            return args;
        }

        private void OnSendCompleted(object? sender, SocketAsyncEventArgs args) => FinishSend(args);

        private ValueTask FinishSend(SocketAsyncEventArgs args, Exception? failure = null)
        {
            lock (gate)
            {
                var op = (SendOperation)args.UserToken!;
                if (failure == null && args.SocketError != SocketError.Success)
                    failure = new SocketException((int)args.SocketError);
                if (failure == null && args.BytesTransferred != args.Count)
                    failure = new SocketException((int)SocketError.MessageSize);
                if (failure == null) Interlocked.Increment(ref sent);
                var result = op.Pending ? op.Result : null;
                var token = op.CancellationToken;
                args.SetBuffer(null, 0, 0);
                args.RemoteEndPoint = null;
                if (op.Rented != null)
                {
                    ArrayPool<byte>.Shared.Return(op.Rented);
                    Interlocked.Increment(ref returns);
                    Interlocked.Add(ref outstanding, -op.Rented.Length);
                }
                op.Rented = null;
                op.CancellationToken = default;
                if (op.Pending) op.Result = null; // never reuse a promise published to a caller
                op.Pending = false;
                idleSends.Push(args);
                // RunContinuationsAsynchronously ensures no application continuation runs
                // under gate. Completion must imply that every accepted send task is settled.
                ValueTask synchronousResult = default;
                if (result != null)
                {
                    if (failure != null) result.TrySetException(failure);
                    else if (token.IsCancellationRequested) result.TrySetCanceled(token);
                    else result.TrySetResult(true);
                }
                else if (failure != null) synchronousResult = new ValueTask(Task.FromException(failure));
                else if (token.IsCancellationRequested) synchronousResult = new ValueTask(Task.FromCanceled(token));
                inFlight--;
                TryComplete();
                return synchronousResult;
            }
        }

        private sealed class SendOperation
        {
            public byte[]? Rented;
            public CancellationToken CancellationToken;
            public TaskCompletionSource<bool>? Result;
            public bool Pending;
        }

        public TransportStatistics GetStatistics() => new TransportStatistics
        {
            SentPackets = Interlocked.Read(ref sent), ReceivedPackets = Interlocked.Read(ref received),
            RejectedPackets = Interlocked.Read(ref rejected), InFlightSends = GetInFlight(),
            BufferRents = Interlocked.Read(ref rents), BufferReturns = Interlocked.Read(ref returns),
            OutstandingBufferBytes = Interlocked.Read(ref outstanding)
        };

        private int GetInFlight() { lock (gate) return inFlight; }

        public void Dispose() => SignalStop();
        public Task StopAsync() { SignalStop(); return completion.Task; }

        private void SignalStop()
        {
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                socket?.Dispose();
                socket = null;
                TryComplete();
            }
        }

        private void FinishReceive()
        {
            lock (gate)
            {
                if (!receiving) return;
                receiving = false;
                ReleaseReceive();
                TryComplete();
            }
        }

        // gate held; only after the last receive callback has returned.
        private void ReleaseReceive()
        {
            receiveArgs?.Dispose();
            receiveArgs = null;
            if (receiveBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
                Interlocked.Increment(ref returns);
                Interlocked.Add(ref outstanding, -receiveBuffer.Length);
                receiveBuffer = null;
            }
        }

        private void TryComplete()
        {
            if (!stopping || receiving || inFlight != 0) return;
            while (idleSends.Count != 0) idleSends.Pop().Dispose();
            completion.TrySetResult(true);
        }
    }
}
