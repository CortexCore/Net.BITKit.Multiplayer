using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace BITKit.Multiplayer.TouchSocket
{
    /// <summary>Explicit compatibility/performance A/B only. Room wires default to native UDP.</summary>
    public sealed class TouchSocketUdpTransportFactory : ITransportFactory
    {
        public string Name => "touchsocket-udp";
        public ITransport Create() => new TouchSocketUdpTransport();
    }

    internal sealed class TouchSocketUdpTransport : ITransport
    {
        private readonly UdpSession _session = new UdpSession();
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _drained = Completed();
        private int _started, _closed, _inflight, _maxPacket, _maxSends;
        private long _sent, _received, _rejected;
        public TransportCapabilities Capabilities => TransportCapabilities.Unreliable;
        public EndPoint? LocalEndPoint { get; private set; }
        public Task Completion => _completion.Task;
        public event Action<EndPoint, ReadOnlyMemory<byte>, RpcDelivery>? Received;
        public event Action<Exception>? Faulted;

        private static TaskCompletionSource<bool> Completed()
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            result.TrySetResult(true);
            return result;
        }
        public async Task StartAsync(TransportOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null || options.LocalEndPoint == null || options.LocalEndPoint.AddressFamily != AddressFamily.InterNetwork ||
                options.LocalEndPoint.Port < 0 || options.MaxPacketBytes < 1 || options.MaxPacketBytes > 65507 || options.MaxConcurrentSends < 1)
                throw new ArgumentException("Invalid UDP endpoint limits", nameof(options));
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0 || Volatile.Read(ref _closed) != 0)
                throw new InvalidOperationException("UDP endpoint already started or stopped");
            _maxPacket = options.MaxPacketBytes; _maxSends = options.MaxConcurrentSends;
            try
            {
                await _session.SetupAsync(new TouchSocketConfig()
                    .SetBindIPHost(new IPHost(options.LocalEndPoint.Address + ":" + options.LocalEndPoint.Port))
                    .ConfigurePlugins(p => p.AddUdpReceivedPlugin((IUdpSessionBase _, UdpReceivedDataEventArgs e) =>
                    { OnReceive(e.EndPoint, e.Memory); return Task.CompletedTask; }))).ConfigureAwait(false);
                await _session.StartAsync(cancellationToken).ConfigureAwait(false);
                LocalEndPoint = _session.Monitor.Socket.LocalEndPoint;
                if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(TouchSocketUdpTransport));
            }
            catch { Dispose(); throw; }
        }
        private void OnReceive(EndPoint source, ReadOnlyMemory<byte> bytes)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            if (bytes.Length > _maxPacket) { Interlocked.Increment(ref _rejected); return; }
            Interlocked.Increment(ref _received);
            try { Received?.Invoke(source, bytes, RpcDelivery.Unreliable); }
            catch (Exception ex) { try { Faulted?.Invoke(ex); } catch { } }
        }
        public async ValueTask SendAsync(EndPoint destination, ReadOnlyMemory<byte> payload,
            RpcDelivery delivery = RpcDelivery.Unreliable, CancellationToken cancellationToken = default)
        {
            if (delivery != RpcDelivery.Unreliable) throw new NotSupportedException("Legacy UDP cannot provide reliable ordered delivery");
            if (destination == null || payload.Length > _maxPacket)
            { Interlocked.Increment(ref _rejected); throw new ArgumentException("Invalid UDP destination or packet length"); }
            lock (_gate)
            {
                if (_closed != 0 || _started == 0 || LocalEndPoint == null) throw new ObjectDisposedException(nameof(TouchSocketUdpTransport));
                if (_inflight >= _maxSends) { Interlocked.Increment(ref _rejected); throw new RpcException(RpcError.LimitExceeded, "Legacy UDP send capacity exceeded"); }
                if (_inflight++ == 0) _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                // This async operation borrows payload through completion, like the native contract.
                await _session.SendAsync(destination, payload, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
            finally
            {
                lock (_gate) if (--_inflight == 0) _drained.TrySetResult(true);
            }
        }
        public TransportStatistics GetStatistics() => new TransportStatistics
        {
            SentPackets = Interlocked.Read(ref _sent), ReceivedPackets = Interlocked.Read(ref _received),
            RejectedPackets = Interlocked.Read(ref _rejected), InFlightSends = Volatile.Read(ref _inflight)
            // TouchSocket's private receive loop allocates its own 64KiB array per packet;
            // this adapter cannot account for or pool those third-party allocations.
        };
        public Task StopAsync() { Dispose(); return Completion; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _ = CloseAsync(); // one task per endpoint, never one task per datagram
        }
        private async Task CloseAsync()
        {
            try
            {
                var stop = await _session.StopAsync().ConfigureAwait(false);
                if (!stop.IsSuccess) throw new InvalidOperationException("Legacy TouchSocket UDP stop failed");
                Task drained; lock (_gate) drained = _drained.Task;
                await drained.ConfigureAwait(false);
                _session.Dispose();
                _completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                try { _session.Dispose(); } catch { }
                _completion.TrySetException(ex);
            }
        }
    }
}
