using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    public sealed partial class RpcContextService
    {
        private abstract class Pending
        {
            public RpcContextService Owner = null!;
            public Connection Connection = null!;
            public uint Id, Target, Method;
            public long Deadline;
            public CancellationToken Token;
            public CancellationTokenRegistration Registration;
            public abstract void Reply(NetRpcModel model);
            public abstract void Fail(Exception error);
        }
        private sealed class Pending<T> : Pending, IUniTaskSource<T>
        {
            private UniTaskCompletionSourceCore<T> _source;
            private bool _ignoreResult, _raw;
            private int _consumed;
            public short Version => _source.Version;
            public UniTask<T> Begin(bool ignoreResult, bool raw)
            { _consumed = 0; _ignoreResult = ignoreResult; _raw = raw; return new UniTask<T>(this, _source.Version); }
            public override void Reply(NetRpcModel model)
            {
                T value;
                try
                {
                    if (_raw) value = (T)(object)new NetRpcModel(model.Kind, model.TargetId, model.MethodId, model.RequestId,
                        model.ArgumentCount, model.Payload.ToArray(), model.Scope);
                    else if (_ignoreResult) value = default!;
                    else
                    {
                        using var reader = NetMessageReader.Rent(model.Payload, model.ArgumentCount);
                        value = reader.Read<T>(); reader.Complete();
                    }
                }
                catch (Exception error) { _source.TrySetException(error); return; }
                // UniTask continuations may run inline. Never signal while holding a runtime gate.
                _source.TrySetResult(value);
            }
            public override void Fail(Exception error) => _source.TrySetException(error);
            public T GetResult(short token)
            {
                if (_source.GetStatus(token) == UniTaskStatus.Pending) throw new InvalidOperationException("RPC is still pending.");
                if (Interlocked.Exchange(ref _consumed, 1) != 0) throw new InvalidOperationException("RPC UniTask has already been consumed.");
                try { return _source.GetResult(token); }
                finally { _source.Reset(); Owner.RecycleRequest(this); }
            }
            void IUniTaskSource.GetResult(short token) => GetResult(token);
            public UniTaskStatus GetStatus(short token) => _source.GetStatus(token);
            public UniTaskStatus UnsafeGetStatus() => _source.UnsafeGetStatus();
            public void OnCompleted(Action<object> continuation, object state, short token) => _source.OnCompleted(continuation, state, token);
        }

        private readonly object _requestGate = new();
        private readonly Dictionary<uint, Pending> _pending = new();
        private readonly Dictionary<Type, Stack<Pending>> _requestPools = new();
        private readonly Stack<List<Pending>> _completionBatches = new();
        private Timer? _requestTimer;

        internal UniTask<T> StartRequest<T>(NetRpcModel model, CancellationToken token, bool ignoreResult = false, bool raw = false)
            => StartRequestCore<T>(model, token, ignoreResult, raw, out _);
        internal UniTask StartVoidRequest(NetRpcModel model, CancellationToken token)
        {
            var result = StartRequestCore<byte>(model, token, true, false, out var source);
            return source == null ? result.AsUniTask() : new UniTask(source, source.Version);
        }
        private UniTask<T> StartRequestCore<T>(NetRpcModel model, CancellationToken token, bool ignoreResult, bool raw, out Pending<T>? source)
        {
            source = null;
            try
            {
                CheckAlive(); token.ThrowIfCancellationRequested();
                if (IsServer) return LocalRequest<T>(model, token, ignoreResult, raw);
                var timeout = RequestTimeout;
                if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
                    throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
                if (!_connections.TryGetValue(1, out var connection)) throw new RpcException(RpcError.Disconnected, "Host disconnected.");
                uint id; UniTask<T> result; Pending<T> waiter;
                lock (_requestGate)
                {
                    CheckAlive();
                    if (!_connections.TryGetValue(1, out var current) || !ReferenceEquals(connection, current))
                        throw new RpcException(RpcError.Disconnected, "Host disconnected.");
                    if (_pending.Count >= 4096) throw new RpcException(RpcError.LimitExceeded, "Pending request limit exceeded.");
                    var pending = _requestPools.TryGetValue(typeof(T), out var pool) && pool.Count > 0 ? (Pending<T>)pool.Pop() : new Pending<T>();
                    do { id = NextRequestId(); } while (_pending.ContainsKey(id));
                    pending.Owner = this; pending.Id = id; pending.Target = model.TargetId; pending.Method = model.MethodId;
                    pending.Connection = connection; pending.Token = token;
                    pending.Deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
                    result = pending.Begin(ignoreResult, raw); waiter = pending;
                    _pending.Add(id, pending);
                    if (_requestTimer == null)
                    {
                        if (ExecutionContext.IsFlowSuppressed()) _requestTimer = new Timer(static state => ((RpcContextService)state!).ExpireRequests(), this, Timeout.Infinite, Timeout.Infinite);
                        else using (ExecutionContext.SuppressFlow()) _requestTimer = new Timer(static state => ((RpcContextService)state!).ExpireRequests(), this, Timeout.Infinite, Timeout.Infinite);
                    }
                    if (_pending.Count == 1) _requestTimer.Change(10, 10);
                }
                // Register outside the gate too: Register may synchronously invoke a cancelled callback.
                if (token.CanBeCanceled) waiter.Registration = token.RegisterWithoutCaptureExecutionContext(static state =>
                { var p = (Pending)state!; p.Owner.CancelRequest(p); }, waiter);
                var call = new NetRpcModel(NetRpcMessageKind.Call, model.TargetId, model.MethodId, id, model.ArgumentCount, model.Payload, Scope);
                var send = SendFrame(1, call, cancellationToken: token, expected: connection);
                if (send.Status == UniTaskStatus.Succeeded) send.GetAwaiter().GetResult();
                else ObserveRequestSend(send, id, connection).Forget();
                source = waiter; return result;
            }
            catch (OperationCanceledException error) { return UniTask.FromCanceled<T>(error.CancellationToken); }
            catch (Exception error) { return UniTask.FromException<T>(error); }
        }
        private async UniTask<T> LocalRequest<T>(NetRpcModel model, CancellationToken token, bool ignoreResult, bool raw)
        {
            token.ThrowIfCancellationRequested();
            var response = await Execute(1, model, false);
            if (raw) return (T)(object)response;
            if (ignoreResult) return default!;
            using var reader = NetMessageReader.Rent(response.Payload, response.ArgumentCount);
            var result = reader.Read<T>(); reader.Complete(); return result;
        }
        private async UniTaskVoid ObserveRequestSend(UniTask send, uint id, Connection connection)
        {
            try { await send; }
            catch (Exception error)
            {
                Pending? failed = null;
                lock (_requestGate)
                    if (_pending.TryGetValue(id, out var pending) && ReferenceEquals(pending.Connection, connection))
                    { RemoveRequest(id); failed = pending; }
                failed?.Fail(error);
            }
        }
        private void CompleteRequest(NetRpcModel model, Connection connection)
        {
            Pending pending;
            lock (_requestGate)
            {
                if (!_pending.TryGetValue(model.RequestId, out pending!) || !ReferenceEquals(pending.Connection, connection) ||
                    pending.Target != model.TargetId || pending.Method != model.MethodId) return;
                RemoveRequest(model.RequestId);
            }
            if (model.Kind != NetRpcMessageKind.Error) { pending.Reply(model); return; }
            Exception failure;
            try
            {
                using var reader = NetMessageReader.Rent(model.Payload, model.ArgumentCount);
                var code = (RpcError)reader.Read<int>(); var message = reader.Read<string>(); reader.Complete();
                failure = new RpcException(code, message);
            }
            catch (Exception error) { failure = error; }
            pending.Fail(failure);
        }
        private void RemoveRequest(uint id)
        {
            _pending.Remove(id);
            if (_pending.Count == 0) _requestTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        private void CancelRequest(Pending pending)
        {
            bool removed = false; CancellationToken token = default;
            lock (_requestGate)
                if (_pending.TryGetValue(pending.Id, out var current) && ReferenceEquals(pending, current))
                { token = pending.Token; RemoveRequest(pending.Id); removed = true; }
            if (removed) pending.Fail(new OperationCanceledException(token));
        }
        private List<Pending>? CollectRequests(bool expiredOnly, Connection? connection)
        {
            lock (_requestGate)
            {
                List<Pending>? batch = null; var now = Stopwatch.GetTimestamp();
                foreach (var pair in _pending)
                {
                    var p = pair.Value;
                    if (expiredOnly ? now < p.Deadline : connection != null && !ReferenceEquals(p.Connection, connection)) continue;
                    batch ??= _completionBatches.Count > 0 ? _completionBatches.Pop() : new List<Pending>();
                    batch.Add(p);
                }
                if (batch != null) foreach (var p in batch) RemoveRequest(p.Id);
                return batch;
            }
        }
        private void ReturnBatch(List<Pending> batch)
        { batch.Clear(); lock (_requestGate) if (!_disposed && _completionBatches.Count < 8) _completionBatches.Push(batch); }
        private void ExpireRequests()
        {
            var batch = CollectRequests(true, null); if (batch == null) return;
            try
            {
                foreach (var p in batch)
                    try { p.Fail(p.Token.IsCancellationRequested ? new OperationCanceledException(p.Token) :
                        new RpcException(RpcError.Timeout, "RPC result timeout; remote execution may have occurred.")); }
                    catch (Exception error) { Report(error); }
            }
            finally { ReturnBatch(batch); }
        }
        private void FailRequests(Connection? connection, RpcError code, string message)
        {
            var batch = CollectRequests(false, connection); if (batch == null) return;
            try { foreach (var p in batch) try { p.Fail(new RpcException(code, message)); } catch (Exception error) { Report(error); } }
            finally { ReturnBatch(batch); }
        }
        private void RecycleRequest<T>(Pending<T> pending)
        {
            pending.Registration.Dispose(); pending.Registration = default; pending.Token = default; pending.Connection = null!;
            lock (_requestGate)
            {
                if (_disposed) return;
                if (!_requestPools.TryGetValue(typeof(T), out var pool)) _requestPools.Add(typeof(T), pool = new Stack<Pending>());
                if (pool.Count < 256) pool.Push(pending);
            }
        }
        private void DisposeRequests()
        { lock (_requestGate) { _requestTimer?.Dispose(); _requestTimer = null; _requestPools.Clear(); _completionBatches.Clear(); } }
    }
}
