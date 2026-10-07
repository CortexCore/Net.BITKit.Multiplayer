using System;

namespace BITKit.Multiplayer
{
    public enum RpcError
    {
        Unauthorized, MissingTarget, MissingMethod, InvalidPayload, RemoteFault,
        Timeout, Disconnected, Disposed, InvalidRole, LimitExceeded
    }

    public sealed class RpcException : Exception
    {
        public RpcException(RpcError error, string message) : base(message) => Error = error;
        public RpcError Error { get; }
    }
}
