#if !NET8_0_OR_GREATER
using System;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    // netstandard2.1 has no SocketAddress ValueTask overload. Reuse the event args and
    // waiter instead of allocating a UdpClient result array / Task for every datagram.
    internal sealed class NetRpcDatagramReceiver : IDisposable, IUniTaskSource<SocketReceiveFromResult>
    {
        private readonly Socket _socket;
        private readonly SocketAsyncEventArgs _args;
        private UniTaskCompletionSourceCore<SocketReceiveFromResult> _completion;
        public NetRpcDatagramReceiver(Socket socket, byte[] buffer, EndPoint template)
        {
            _socket = socket;
            _args = new SocketAsyncEventArgs { RemoteEndPoint = template };
            _args.SetBuffer(buffer, 0, 65536); _args.Completed += Completed;
        }
        public UniTask<SocketReceiveFromResult> Receive()
        {
            _completion.Reset();
            try { if (!_socket.ReceiveFromAsync(_args)) Complete(); }
            catch (Exception error) { _completion.TrySetException(error); }
            return new UniTask<SocketReceiveFromResult>(this, _completion.Version);
        }
        private void Completed(object? sender, SocketAsyncEventArgs args) => Complete();
        private void Complete()
        {
            if (_args.SocketError != SocketError.Success) _completion.TrySetException(new SocketException((int)_args.SocketError));
            else _completion.TrySetResult(new SocketReceiveFromResult { ReceivedBytes = _args.BytesTransferred, RemoteEndPoint = _args.RemoteEndPoint! });
        }
        public SocketReceiveFromResult GetResult(short token) => _completion.GetResult(token);
        void IUniTaskSource.GetResult(short token) => GetResult(token);
        public UniTaskStatus GetStatus(short token) => _completion.GetStatus(token);
        public UniTaskStatus UnsafeGetStatus() => _completion.UnsafeGetStatus();
        public void OnCompleted(Action<object> continuation, object state, short token) => _completion.OnCompleted(continuation, state, token);
        public void Dispose() { _args.Completed -= Completed; _args.Dispose(); }
    }
}
#endif
