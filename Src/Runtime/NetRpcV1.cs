using Cysharp.Threading.Tasks;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace BITKit.Multiplayer.NetRpc
{
    public enum NetRpcMessageKind : byte
    {
        Call = 1, Return = 2, MapRequest = 3, MapResponse = 4, Error = 5,
        Component = 6, SyncSnapshot = 7, SyncOperation = 8, SyncRequest = 9, FastCall = 10
    }

    /// <summary>Send completion ends the payload loan. Received memory is borrowed until its synchronous callback returns.</summary>
    public interface ITransport
    {
        event Action<ReadOnlyMemory<byte>>? OnReceived;
        UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
        UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    }
    public interface ITransportLifetime { event Action? Closed; }

    public readonly struct NetRpcModel
    {
        public NetRpcModel(NetRpcMessageKind kind, uint targetId, uint methodId, uint requestId,
            int argumentCount, ReadOnlyMemory<byte> payload, ulong scope = 0)
        { Kind = kind; TargetId = targetId; MethodId = methodId; RequestId = requestId;
          ArgumentCount = argumentCount; Payload = payload; Scope = scope; }
        public NetRpcMessageKind Kind { get; }
        public uint TargetId { get; }
        public uint MethodId { get; }
        public uint RequestId { get; }
        public int ArgumentCount { get; }
        public ReadOnlyMemory<byte> Payload { get; }
        public ulong Scope { get; }
    }

    public static class NetRpcCodec
    {
        public const int HeaderBytes = 25;
        public const int MaxPayloadBytes = 1024 * 1024;
        public static byte[] Encode(NetRpcModel model)
        { var bytes = new byte[HeaderBytes + model.Payload.Length]; Write(model, bytes); return bytes; }
        internal static void Write(NetRpcModel model, Span<byte> span)
        {
            if (model.ArgumentCount < 0 || model.ArgumentCount > 16384 || model.Payload.Length > MaxPayloadBytes)
                throw new RpcException(RpcError.LimitExceeded, "NetRpc frame exceeds limits.");
            span[0] = (byte)model.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), model.TargetId);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5), model.MethodId);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(9), model.RequestId);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(13), model.ArgumentCount);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(17), model.Scope);
            model.Payload.Span.CopyTo(span.Slice(HeaderBytes));
        }
        public static NetRpcModel Decode(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + MaxPayloadBytes)
                throw new RpcException(RpcError.InvalidPayload, "Invalid NetRpc frame length.");
            var span = bytes.Span; var kind = (NetRpcMessageKind)span[0];
            if (kind < NetRpcMessageKind.Call || kind > NetRpcMessageKind.FastCall)
                throw new RpcException(RpcError.InvalidPayload, "Unknown NetRpc command.");
            var count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(13));
            if (count < 0 || count > 16384) throw new RpcException(RpcError.InvalidPayload, "Invalid argument count.");
            return new NetRpcModel(kind, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1)),
                BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(5)), BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(9)),
                count, bytes.Slice(HeaderBytes), BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(17)));
        }
    }

    /// <summary>Pooled, bounded MessagePack writer. Generic writes do not box values or allocate byte arrays.</summary>
    public sealed class NetMessageBag : IBufferWriter<byte>, IDisposable
    {
        [ThreadStatic] private static Stack<NetMessageBag>? _pool;
        private byte[]? _bytes;
        private int _length, _count;
        public NetMessageBag() { _bytes = ArrayPool<byte>.Shared.Rent(256); }
        public static NetMessageBag Pool()
        {
            var bag = _pool != null && _pool.Count != 0 ? _pool.Pop() : new NetMessageBag();
            bag._bytes ??= ArrayPool<byte>.Shared.Rent(256); bag._length = bag._count = 0; return bag;
        }
        public int Count => _count;
        public ReadOnlyMemory<byte> Memory => (_bytes ?? throw new ObjectDisposedException(nameof(NetMessageBag))).AsMemory(0, _length);
        public static implicit operator ReadOnlyMemory<byte>(NetMessageBag bag) => bag.Memory;
        internal void Append(NetMessageBag bag)
        { bag.Memory.Span.CopyTo(GetSpan(bag.Memory.Length)); Advance(bag.Memory.Length); _count += bag.Count; }
        public void Write<T>(T value)
        {
            var at = _length; AdvancePrefix();
            try { MessagePackSerializer.Serialize(this, value, NetSerialization.Options); }
            catch { _length = at; throw; }
            BinaryPrimitives.WriteInt32LittleEndian(_bytes!.AsSpan(at, 4), _length - at - 4); _count++;
        }
        public void Write(Type type, object? value)
        {
            var at = _length; AdvancePrefix();
            try { MessagePackSerializer.Serialize(type, this, value, NetSerialization.Options); }
            catch { _length = at; throw; }
            BinaryPrimitives.WriteInt32LittleEndian(_bytes!.AsSpan(at, 4), _length - at - 4); _count++;
        }
        private void AdvancePrefix() { GetSpan(4).Slice(0, 4).Clear(); Advance(4); }
        public void Advance(int count)
        {
            if (_bytes == null) throw new ObjectDisposedException(nameof(NetMessageBag));
            if (count < 0 || count > _bytes.Length - _length || count > NetRpcCodec.MaxPayloadBytes - _length)
                throw new RpcException(RpcError.LimitExceeded, "Message bag limit exceeded.");
            _length += count;
        }
        private void Ensure(int hint)
        {
            if (_bytes == null) throw new ObjectDisposedException(nameof(NetMessageBag));
            hint = Math.Max(1, hint);
            if (hint > NetRpcCodec.MaxPayloadBytes - _length) throw new RpcException(RpcError.LimitExceeded, "Message bag limit exceeded.");
            if (_bytes.Length - _length >= hint) return;
            var next = ArrayPool<byte>.Shared.Rent(Math.Min(NetRpcCodec.MaxPayloadBytes, Math.Max(_length + hint, _bytes.Length * 2)));
            _bytes.AsSpan(0, _length).CopyTo(next); ArrayPool<byte>.Shared.Return(_bytes); _bytes = next;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _bytes!.AsMemory(_length); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _bytes!.AsSpan(_length); }
        public void Dispose()
        {
            var bytes = _bytes; if (bytes == null) return;
            _bytes = null; _length = _count = 0; ArrayPool<byte>.Shared.Return(bytes);
            var pool = _pool ??= new Stack<NetMessageBag>(); if (pool.Count < 32) pool.Push(this);
        }
    }

    public static class NetSerialization
    {
        public static MessagePackSerializerOptions Options { get; } = MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance).WithSecurity(MessagePackSecurity.UntrustedData);
    }

    public sealed class NetMessageReader : IDisposable
    {
        [ThreadStatic] private static Stack<NetMessageReader>? _pool;
        private ReadOnlyMemory<byte> _memory;
        private int _remaining;
        private bool _disposed;
        public NetMessageReader(ReadOnlyMemory<byte> memory, int count) { _memory = memory; _remaining = count; }
        internal static NetMessageReader Rent(ReadOnlyMemory<byte> memory, int count)
        {
            var reader = _pool != null && _pool.Count != 0 ? _pool.Pop() : new NetMessageReader(default, 0);
            reader._memory = memory; reader._remaining = count; reader._disposed = false; return reader;
        }
        private ReadOnlyMemory<byte> Take()
        {
            if (_remaining <= 0 || _memory.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated argument.");
            var size = BinaryPrimitives.ReadInt32LittleEndian(_memory.Span);
            if (size < 0 || size > _memory.Length - 4) throw new RpcException(RpcError.InvalidPayload, "Invalid argument size.");
            var item = _memory.Slice(4, size); _memory = _memory.Slice(4 + size); _remaining--; return item;
        }
        public T Read<T>()
        {
            var reader = new MessagePackReader(Take());
            var value = MessagePackSerializer.Deserialize<T>(ref reader, NetSerialization.Options);
            if (!reader.End) throw new RpcException(RpcError.InvalidPayload, "Trailing MessagePack value bytes."); return value;
        }
        public object? Read(Type type)
        {
            var reader = new MessagePackReader(Take());
            var value = MessagePackSerializer.Deserialize(type, ref reader, NetSerialization.Options);
            if (!reader.End) throw new RpcException(RpcError.InvalidPayload, "Trailing MessagePack value bytes."); return value;
        }
        public void Complete() { if (_remaining != 0 || !_memory.IsEmpty) throw new RpcException(RpcError.InvalidPayload, "Trailing arguments."); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _memory = default; _remaining = 0;
            var pool = _pool ??= new Stack<NetMessageReader>(); if (pool.Count < 32) pool.Push(this);
        }
    }

    public delegate UniTask<NetMessageBag?> NetRpcReceiver(object instance, NetMessageReader reader);

    public static class NetRpcResults
    {
        public static readonly UniTask<NetMessageBag?> Void = UniTask.FromResult<NetMessageBag?>(null);
        public static async UniTask<NetMessageBag?> CompleteTask(Task task) { await task.ConfigureAwait(false); return null; }
        public static async UniTask<NetMessageBag?> CompleteTask<T>(Task<T> task)
        { var value = await task.ConfigureAwait(false); var bag = NetMessageBag.Pool(); try { bag.Write(value); return bag; } catch { bag.Dispose(); throw; } }
        public static async UniTask<NetMessageBag?> CompleteValueTask(ValueTask task) { await task.ConfigureAwait(false); return null; }
        public static async UniTask<NetMessageBag?> CompleteValueTask<T>(ValueTask<T> task)
        { var value = await task.ConfigureAwait(false); var bag = NetMessageBag.Pool(); try { bag.Write(value); return bag; } catch { bag.Dispose(); throw; } }
        public static async UniTask<NetMessageBag?> CompleteUniTask(UniTask task) { await task; return null; }
        public static async UniTask<NetMessageBag?> CompleteUniTask<T>(UniTask<T> task)
        { var value = await task; var bag = NetMessageBag.Pool(); try { bag.Write(value); return bag; } catch { bag.Dispose(); throw; } }
    }

    public sealed class RpcContext
    {
        internal readonly RpcContextService Owner;
        internal RpcContext(RpcContextService owner, uint targetId, bool isServer) { Owner = owner; TargetId = targetId; IsServer = isServer; }
        public uint TargetId { get; }
        public bool IsServer { get; }
        public bool IsRemoteInvocation => NetRpcCallContext.Current != null;
        public async UniTask Send(NetRpcModel model, CancellationToken cancellationToken = default) =>
            await Owner.DispatchAsync(model, false, cancellationToken);
        public async UniTask<object?> SendRequest(NetRpcModel model, Type resultType, CancellationToken cancellationToken = default)
        {
            var response = await Owner.DispatchAsync(model, true, cancellationToken);
            if (resultType == typeof(void)) return null;
            using var reader = NetMessageReader.Rent(response.Payload, response.ArgumentCount); var value = reader.Read(resultType); reader.Complete(); return value;
        }
        public UniTask Request(NetRpcModel model, CancellationToken cancellationToken = default) => Owner.StartVoidRequest(model, cancellationToken);
        public UniTask<T> Request<T>(NetRpcModel model, CancellationToken cancellationToken = default) => Owner.StartRequest<T>(model, cancellationToken);
        public Task RequestTask(NetRpcModel model, CancellationToken cancellationToken = default) => AsCompatibleTask(Request(model, cancellationToken));
        public Task<T> RequestTask<T>(NetRpcModel model, CancellationToken cancellationToken = default) => AsCompatibleTask(Request<T>(model, cancellationToken));
        private static async Task AsCompatibleTask(UniTask operation) => await operation;
        private static async Task<T> AsCompatibleTask<T>(UniTask<T> operation) => await operation;
        public ValueTask RequestValue(NetRpcModel model, CancellationToken cancellationToken = default) =>
            new ValueTask(RequestTask(model, cancellationToken));
        public ValueTask<T> RequestValue<T>(NetRpcModel model, CancellationToken cancellationToken = default) =>
            new ValueTask<T>(RequestTask<T>(model, cancellationToken));
        public void Notify(NetRpcModel model) => Owner.SendNotification(model);
        public void NotifyFast(NetRpcModel model) => Owner.SendNotification(new NetRpcModel(NetRpcMessageKind.FastCall, model.TargetId, model.MethodId, 0, model.ArgumentCount, model.Payload, model.Scope), true);
        public T GetValue<T>(uint propertyId) => Owner.GetValue<T>(TargetId, propertyId);
        public NetworkList<T> GetList<T>(uint propertyId) => Owner.GetList<T>(TargetId, propertyId);
        public NetworkDictionary<TKey, TValue> GetDictionary<TKey, TValue>(uint propertyId) where TKey : notnull => Owner.GetDictionary<TKey, TValue>(TargetId, propertyId);
    }

    public sealed class NetRpcCallContext
    {
        private static readonly AsyncLocal<NetRpcCallContext?> Slot = new();
        public static NetRpcCallContext? Current => Slot.Value;
        internal static NetRpcCallContext? Set(NetRpcCallContext? context) { var previous = Slot.Value; Slot.Value = context; return previous; }
        internal NetRpcCallContext(uint sender, uint target) { SenderPeerId = sender; TargetId = target; }
        public uint SenderPeerId { get; }
        public uint TargetId { get; }
    }

    public sealed partial class RpcContextService : IDisposable
    {
        private sealed class Method
        {
            public string Name = "";
            public SendTo Route;
            public RpcDelivery Delivery;
            public bool ReturnsCompletion;
            public bool WovenReceiver;
            public NetRpcReceiver Receive = null!;
        }
        private sealed class Target
        {
            public uint Id;
            public object Instance = null!;
            public Type Type = null!;
            public readonly Dictionary<uint, Method> Methods = new();
        }
        private sealed class Connection
        {
            public ITransport Transport = null!;
            public Action<ReadOnlyMemory<byte>> Handler = null!;
            public Action? Closed;
        }
        private readonly IServiceProvider _services;
        private readonly ConcurrentDictionary<uint, Target> _targets = new();
        private readonly ConcurrentDictionary<uint, Connection> _connections = new();
        private readonly object _connectionGate = new();
        private KeyValuePair<uint, Connection>[] _peerSnapshot = Array.Empty<KeyValuePair<uint, Connection>>();
        private readonly ConcurrentDictionary<(uint Peer, uint Target, uint Method), ConcurrentQueue<NetRpcModel>> _awaitingMaps = new();
        private readonly ConcurrentDictionary<Type, byte> _allowedTypes = new();
        private int _requestId, _mapCalls;
        private readonly object _mapGate = new();
        private volatile bool _disposed;
        public RpcContextService(IServiceProvider services, ITransport transport, bool isServer) : this(services, isServer)
        { AttachPeer(isServer ? 2u : 1u, transport); }
        public RpcContextService(IServiceProvider services, bool isServer, ulong scope = 0)
        { _services = services ?? throw new ArgumentNullException(nameof(services)); IsServer = isServer; Scope = scope; }
        public bool IsServer { get; }
        public ulong Scope { get; }
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public Func<uint, uint, uint, bool>? Authorize { get; set; }
        public event Action<Exception>? Faulted;
        /// <summary>Logical peer loss, including Relay peers. Raised once after removal, outside runtime locks.</summary>
        public event Action<uint>? PeerDisconnected;
        public void AttachPeer(uint peerId, ITransport transport)
        {
            CheckAlive(); if (peerId == 0 || IsServer && peerId == 1 || !IsServer && peerId != 1) throw new ArgumentOutOfRangeException(nameof(peerId));
            var connection = new Connection { Transport = transport };
            connection.Handler = bytes => Receive(peerId, connection, bytes);
            lock (_connectionGate)
            {
                CheckAlive();
                if (!_connections.TryAdd(peerId, connection)) throw new InvalidOperationException("Peer already attached.");
                Volatile.Write(ref _peerSnapshot, _connections.ToArray());
                if (transport is ITransportLifetime lifetime) { connection.Closed = () => DetachPeer(peerId, connection); lifetime.Closed += connection.Closed; }
                transport.OnReceived += connection.Handler;
            }
        }
        public void DetachPeer(uint peerId) => DetachPeer(peerId, null);
        private void DetachPeer(uint peerId, Connection? expected)
        {
            bool removed; Connection? connection;
            lock (_connectionGate)
            {
                if (expected != null && (!_connections.TryGetValue(peerId, out var current) || !ReferenceEquals(current, expected))) return;
                removed = _connections.TryRemove(peerId, out connection);
                if (removed)
                {
                    Volatile.Write(ref _peerSnapshot, _connections.ToArray());
                    connection!.Transport.OnReceived -= connection.Handler;
                    if (connection.Transport is ITransportLifetime lifetime) lifetime.Closed -= connection.Closed;
                }
            }
            if (removed && !IsServer) FailRequests(connection!, RpcError.Disconnected, "Host disconnected.");
            foreach (var key in _awaitingMaps.Keys.Where(k => k.Peer == peerId))
                if (_awaitingMaps.TryRemove(key, out var queue)) Interlocked.Add(ref _mapCalls, -queue.Count);
            if (removed && !_disposed) { try { PeerDisconnected?.Invoke(peerId); } catch (Exception error) { Report(error); } }
        }
        public void AllowContract(Type type) { CheckAlive(); _allowedTypes.TryAdd(type, 0); }
        public RpcContext CreateContext(uint targetId) { CheckAlive(); return new RpcContext(this, targetId, IsServer); }
        public RpcContext RegisterTarget(uint targetId, object instance) => RegisterTarget(targetId, instance, instance.GetType());
        public RpcContext RegisterTarget(uint targetId, object instance, Type contract)
        {
            CheckAlive(); if (targetId == 0 || !contract.IsInstanceOfType(instance)) throw new ArgumentException("Invalid target/contract.");
            AllowContract(contract);
            var target = new Target { Id = targetId, Instance = instance, Type = contract };
            foreach (var info in ContractMethods(contract))
            {
                var implementation = info;
                if (contract.IsInterface)
                {
                    var map = instance.GetType().GetInterfaceMap(info.DeclaringType!);
                    var index = Array.IndexOf(map.InterfaceMethods, info); if (index >= 0) implementation = map.TargetMethods[index];
                }
                var attribute = info.GetCustomAttribute<RpcAttribute>() ?? implementation.GetCustomAttribute<RpcAttribute>();
                if (!contract.IsInterface && attribute == null) continue;
                var id = RpcMethodId(contract, info);
                var implementationId = RpcMethodId(implementation.DeclaringType!, implementation);
                var generated = implementation.DeclaringType!.GetMethod("__netrpc_recv_" + implementationId, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var descriptor = new Method { Name = MethodSignature(info), Route = attribute?.To ?? SendTo.Host,
                    Delivery = attribute?.Delivery ?? RpcDelivery.Reliable,
                    ReturnsCompletion = info.ReturnType != typeof(void),
                    WovenReceiver = generated != null,
                    Receive = generated != null ? (NetRpcReceiver)generated.CreateDelegate(typeof(NetRpcReceiver)) : CreateReflectionReceiver(info) };
                target.Methods.Add(id, descriptor);
                if (generated != null && implementationId != id) target.Methods.Add(implementationId, descriptor);
            }
            if (contract.IsInterface)
                foreach (var info in ContractMethods(instance.GetType()))
                {
                    var attribute = info.GetCustomAttribute<RpcAttribute>(); if (attribute == null) continue;
                    var id = RpcMethodId(info.DeclaringType!, info); if (target.Methods.ContainsKey(id)) continue;
                    var generated = info.DeclaringType!.GetMethod("__netrpc_recv_" + id, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    target.Methods.Add(id, new Method { Name = MethodSignature(info), Route = attribute.To, Delivery = attribute.Delivery,
                        ReturnsCompletion = info.ReturnType != typeof(void), WovenReceiver = generated != null,
                        Receive = generated != null ? (NetRpcReceiver)generated.CreateDelegate(typeof(NetRpcReceiver)) : CreateReflectionReceiver(info) });
                }
            if (!_targets.TryAdd(targetId, target)) throw new InvalidOperationException("Target already registered.");
            bool attached = false;
            try { NetRpcDispatch.Attach(instance, this, targetId); attached = true; RegisterState(targetId, instance, contract); }
            catch { _targets.TryRemove(targetId, out _); RemoveState(targetId); if (attached) NetRpcDispatch.Detach(instance); throw; }
            return CreateContext(targetId);
        }
        public bool RemoveTarget(uint targetId)
        {
            if (!_targets.TryRemove(targetId, out var target)) return false;
            NetRpcDispatch.Detach(target.Instance); RemoveState(targetId); return true;
        }
        public void SetGeneratedReceiver(uint targetId, uint methodId, NetRpcReceiver receiver)
        { var descriptor = _targets[targetId].Methods[methodId]; if (!descriptor.WovenReceiver) descriptor.Receive = receiver; }
        internal static IEnumerable<MethodInfo> ContractMethods(Type contract) =>
            (contract.IsInterface ? new[] { contract }.Concat(contract.GetInterfaces()) : new[] { contract })
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).Where(m => !m.IsSpecialName).GroupBy(MethodSignature).Select(g => g.First());
        public static uint ContractId(Type type) => StableId(type.FullName ?? type.Name);
        public static uint PropertyId(Type contract, string name) => StableId((contract.FullName ?? contract.Name) + "/property/" + name);
        public static uint StableId(string text) { unchecked { uint hash = 2166136261; foreach (var c in text) { hash ^= c; hash *= 16777619; } return hash == 0 ? 1u : hash; } }
        public static string TypeIdentity(Type type) => type.IsArray ? TypeIdentity(type.GetElementType()!) + "[]" : type.IsGenericType
            ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + ">" : type.FullName ?? type.Name;
        internal static string MethodSignature(MethodInfo method) => method.Name + "/" + string.Join(",", method.GetParameters().Select(p => TypeIdentity(p.ParameterType))) + "/" + TypeIdentity(method.ReturnType);
        public static uint RpcMethodId(Type type, MethodInfo method) => StableId(type.FullName + "/" + MethodSignature(method));
        private static NetRpcReceiver CreateReflectionReceiver(MethodInfo info)
        {
            var parameters = info.GetParameters(); var pool = new ConcurrentBag<object?[]>();
            var raw = info.DeclaringType!.GetMethod("__netrpc_body_" + RpcMethodId(info.DeclaringType, info), BindingFlags.Instance | BindingFlags.NonPublic) ?? info;
            return async (instance, reader) =>
            {
                if (!pool.TryTake(out var args)) args = new object?[parameters.Length];
                try
                {
                    for (int i = 0; i < args.Length; i++) args[i] = reader.Read(parameters[i].ParameterType); reader.Complete();
                    var result = raw.Invoke(instance, args);
                    if (result is UniTask uniTask) { await uniTask; return null; }
                    if (info.ReturnType.IsGenericType && info.ReturnType.GetGenericTypeDefinition() == typeof(UniTask<>))
                        result = await (UniTask<object?>)typeof(RpcContextService).GetMethod(nameof(AwaitReflectedUniTask), BindingFlags.Static | BindingFlags.NonPublic)!
                            .MakeGenericMethod(info.ReturnType.GetGenericArguments()[0]).Invoke(null, new[] { result })!;
                    if (result is ValueTask valueTask) { await valueTask.ConfigureAwait(false); return null; }
                    if (result != null && !(result is Task) && info.ReturnType.GetMethod("AsTask", Type.EmptyTypes) is MethodInfo asTask) result = asTask.Invoke(result, null);
                    if (result is Task task)
                    {
                        await task.ConfigureAwait(false);
                        if (!info.ReturnType.IsGenericType) return null;
                        result = task.GetType().GetProperty("Result")!.GetValue(task);
                    }
                    if (info.ReturnType == typeof(void)) return null;
                    var bag = NetMessageBag.Pool();
                    try { bag.Write(info.ReturnType.IsGenericType ? info.ReturnType.GetGenericArguments()[0] : info.ReturnType, result); return bag; }
                    catch { bag.Dispose(); throw; }
                }
                finally { Array.Clear(args, 0, args.Length); pool.Add(args); }
            };
        }
        private static async UniTask<object?> AwaitReflectedUniTask<T>(object task) => await (UniTask<T>)task;
        internal UniTask<NetRpcModel> DispatchAsync(NetRpcModel model, bool expectsReply, CancellationToken cancellationToken)
        {
            CheckAlive();
            if (IsServer) return Execute(1, model, false);
            if (expectsReply) return StartRequest<NetRpcModel>(model, cancellationToken, raw: true);
            return SendWithoutReply(model, cancellationToken);
        }
        private async UniTask<NetRpcModel> SendWithoutReply(NetRpcModel model, CancellationToken token)
        { await SendFrame(1, new NetRpcModel(NetRpcMessageKind.Call, model.TargetId, model.MethodId, 0, model.ArgumentCount, model.Payload, Scope), cancellationToken: token); return default; }
        internal void SendNotification(NetRpcModel model, bool fast = false)
        { CheckAlive(); if (IsServer) Observe(Execute(1, model, false)); else Observe(SendFrame(1, model, fast)); }
        internal void Report(Exception error) { try { Faulted?.Invoke(error); } catch { } }
        private void Observe(UniTask task)
        {
            if (task.Status == UniTaskStatus.Succeeded) { task.GetAwaiter().GetResult(); return; }
            ObservePending(task).Forget();
        }
        private void Observe<T>(UniTask<T> task)
        { if (task.Status == UniTaskStatus.Succeeded) task.GetAwaiter().GetResult(); else ObservePending(task).Forget(); }
        private async UniTaskVoid ObservePending(UniTask task) { try { await task; } catch (Exception error) { Report(error); } }
        private async UniTaskVoid ObservePending<T>(UniTask<T> task) { try { await task; } catch (Exception error) { Report(error); } }
        private void Receive(uint peer, Connection connection, ReadOnlyMemory<byte> bytes)
        {
            try
            {
                var model = NetRpcCodec.Decode(bytes); if (model.Scope != Scope || _disposed || !_connections.TryGetValue(peer, out var current) || !ReferenceEquals(current, connection)) return;
                EnsureServices();
                switch (model.Kind)
                {
                    case NetRpcMessageKind.Call: case NetRpcMessageKind.FastCall: Observe(ReceiveCall(peer, model, connection)); break;
                    case NetRpcMessageKind.Return: case NetRpcMessageKind.Error:
                        if (!IsServer && peer == 1) CompleteRequest(model, connection);
                        break;
                    case NetRpcMessageKind.MapRequest: Observe(SendMap(peer, model)); break;
                    case NetRpcMessageKind.MapResponse: Observe(ReceiveMap(peer, model, connection)); break;
                    default: Observe(ReceiveState(peer, model, connection)); break;
                }
            }
            catch (Exception error) { Report(error); }
        }
        private async UniTask ReceiveCall(uint peer, NetRpcModel call, Connection connection)
        {
            if (!_connections.TryGetValue(peer, out var current) || !ReferenceEquals(current, connection)) return;
            if (!_targets.TryGetValue(call.TargetId, out var target) || !target.Methods.ContainsKey(call.MethodId))
            {
                if (Interlocked.Increment(ref _mapCalls) > 256) { Interlocked.Decrement(ref _mapCalls); await SendError(peer, call, "RpcMap queue limit exceeded."); return; }
                var key = (peer, call.TargetId, call.MethodId);
                ConcurrentQueue<NetRpcModel> queue;
                lock (_mapGate)
                {
                    queue = _awaitingMaps.GetOrAdd(key, _ => new ConcurrentQueue<NetRpcModel>());
                    queue.Enqueue(new NetRpcModel(call.Kind, call.TargetId, call.MethodId, call.RequestId, call.ArgumentCount, call.Payload.ToArray(), Scope));
                }
                await SendFrame(peer, new NetRpcModel(NetRpcMessageKind.MapRequest, call.TargetId, call.MethodId, call.RequestId, 0, ReadOnlyMemory<byte>.Empty, Scope));
                Observe(ExpireMap(key, queue)); return;
            }
            try { await Execute(peer, call, true, connection); }
            catch (Exception error) { var cause = error.GetBaseException(); await SendError(peer, call, cause.Message, cause is RpcException rpc ? rpc.Error : RpcError.RemoteFault, connection); Report(error); }
        }
        private async UniTask ExpireMap((uint Peer, uint Target, uint Method) key, ConcurrentQueue<NetRpcModel> queue)
        {
            await Task.Delay(RequestTimeout).ConfigureAwait(false);
            if (_awaitingMaps.TryGetValue(key, out var current) && ReferenceEquals(queue, current) && _awaitingMaps.TryRemove(key, out var removed))
                while (removed.TryDequeue(out var call)) { Interlocked.Decrement(ref _mapCalls); if (!_disposed) await SendError(key.Peer, call, "RpcMap timeout."); }
        }
        private async UniTask<NetRpcModel> Execute(uint peer, NetRpcModel call, bool received, Connection? origin = null)
        {
            if (!_targets.TryGetValue(call.TargetId, out var target)) throw new RpcException(RpcError.MissingTarget, "Target not registered.");
            if (!target.Methods.TryGetValue(call.MethodId, out var method)) throw new RpcException(RpcError.MissingMethod, "Method not registered.");
            if (received && !IsServer && method.Route == SendTo.Host || IsServer && peer != 1 && method.Route == SendTo.All)
                throw new RpcException(RpcError.InvalidRole, "Invalid RPC direction.");
            if (Authorize != null && !Authorize(peer, call.TargetId, call.MethodId)) throw new RpcException(RpcError.Unauthorized, "RPC denied.");
            if (method.Delivery == RpcDelivery.Unreliable && call.RequestId != 0) throw new RpcException(RpcError.InvalidPayload, "Unreliable RPC cannot return a result.");
            if (!method.ReturnsCompletion && call.RequestId != 0) throw new RpcException(RpcError.InvalidPayload, "Void RPC cannot request a completion reply.");
            var result = await InvokeReceiver(peer, call, target, method);
            using (result)
            {
                var response = new NetRpcModel(NetRpcMessageKind.Return, call.TargetId, call.MethodId, call.RequestId, result?.Count ?? 0, result?.Memory ?? ReadOnlyMemory<byte>.Empty, Scope);
                if (received && call.RequestId != 0) await SendFrame(peer, response, expected: origin);
                if (IsServer && method.Route == SendTo.All)
                    foreach (var recipient in Volatile.Read(ref _peerSnapshot)) await SendFrame(recipient.Key, call, method.Delivery == RpcDelivery.Unreliable, expected: recipient.Value);
                return received ? default : new NetRpcModel(response.Kind, response.TargetId, response.MethodId, response.RequestId, response.ArgumentCount, response.Payload.ToArray(), Scope);
            }
        }
        private static UniTask<NetMessageBag?> InvokeReceiver(uint peer, NetRpcModel call, Target target, Method method)
        {
            // UniTask builders don't restore the caller's ExecutionContext on suspension.
            // Scope the invocation prefix explicitly; immutable Current can be captured by an async business body.
            var previous = NetRpcCallContext.Set(new NetRpcCallContext(peer, call.TargetId));
            try
            {
                using var reader = NetMessageReader.Rent(call.Payload, call.ArgumentCount);
                var execution = method.Receive(target.Instance, reader); reader.Complete(); return execution;
            }
            finally { NetRpcCallContext.Set(previous); }
        }
        private async UniTask SendFrame(uint peer, NetRpcModel model, bool fast = false, CancellationToken cancellationToken = default, Connection? expected = null)
        {
            CheckAlive(); if (!_connections.TryGetValue(peer, out var connection))
            { if (expected != null) return; throw new RpcException(RpcError.Disconnected, "Peer not connected."); }
            if (expected != null && !ReferenceEquals(expected, connection)) return;
            var scoped = new NetRpcModel(model.Kind, model.TargetId, model.MethodId, model.RequestId, model.ArgumentCount, model.Payload, Scope);
            var length = NetRpcCodec.HeaderBytes + scoped.Payload.Length; var bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                NetRpcCodec.Write(scoped, bytes.AsSpan(0, length));
                if (fast) await connection.Transport.SendFast(bytes.AsMemory(0, length), cancellationToken);
                else await connection.Transport.Send(bytes.AsMemory(0, length), cancellationToken);
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
        }
        private async UniTask SendMap(uint peer, NetRpcModel request)
        {
            Type? type = null; string? signature = null;
            if (_targets.TryGetValue(request.TargetId, out var target) && target.Methods.TryGetValue(request.MethodId, out var method)) { type = target.Type; signature = method.Name; }
            else
            {
                type = _allowedTypes.Keys.SingleOrDefault(t => ContractId(t) == request.TargetId);
                var info = type == null ? null : ContractMethods(type).FirstOrDefault(m => RpcMethodId(type, m) == request.MethodId);
                if (info != null) signature = MethodSignature(info);
            }
            if (type == null || signature == null) { await SendError(peer, request, "RpcMap entry unavailable."); return; }
            using var bag = NetMessageBag.Pool(); bag.Write(type.FullName); bag.Write(signature);
            await SendFrame(peer, new NetRpcModel(NetRpcMessageKind.MapResponse, request.TargetId, request.MethodId, request.RequestId, bag.Count, bag.Memory, Scope));
        }
        private async UniTask ReceiveMap(uint peer, NetRpcModel response, Connection connection)
        {
            var key = (peer, response.TargetId, response.MethodId);
            ConcurrentQueue<NetRpcModel>? queue;
            lock (_mapGate) if (!_awaitingMaps.TryRemove(key, out queue)) return;
            try
            {
                var reader = new NetMessageReader(response.Payload, response.ArgumentCount); var name = reader.Read<string>(); var signature = reader.Read<string>(); reader.Complete();
                var type = _allowedTypes.Keys.SingleOrDefault(t => t.FullName == name);
                // Only locally registered DI types are eligible. Never load arbitrary wire-supplied CLR types.
                if (type == null) type = NetRpcServiceRegistration.FindContract(_services, name);
                var instance = type == null ? null : _services.GetService(type);
                if (instance == null || type == null) throw new RpcException(RpcError.MissingTarget, "RpcMap contract is not registered in DI: " + name);
                lock (_registrationGate) if (!_targets.ContainsKey(response.TargetId)) RegisterTarget(response.TargetId, instance, type);
                if (!_targets[response.TargetId].Methods.TryGetValue(response.MethodId, out var method) || method.Name != signature)
                    throw new RpcException(RpcError.MissingMethod, "RpcMap signature mismatch.");
                while (queue.TryDequeue(out var call)) { Interlocked.Decrement(ref _mapCalls); await ReceiveCall(peer, call, connection); }
            }
            catch (Exception error)
            {
                while (queue.TryDequeue(out var call)) { Interlocked.Decrement(ref _mapCalls); await SendError(peer, call, error.Message); }
                throw;
            }
        }
        private async UniTask SendError(uint peer, NetRpcModel call, string message, RpcError error = RpcError.RemoteFault, Connection? expected = null)
        {
            using var bag = NetMessageBag.Pool(); bag.Write((int)error); bag.Write(message);
            await SendFrame(peer, new NetRpcModel(NetRpcMessageKind.Error, call.TargetId, call.MethodId, call.RequestId, bag.Count, bag.Memory, Scope), expected: expected);
        }
        private uint NextRequestId() { uint id; do { id = unchecked((uint)Interlocked.Increment(ref _requestId)); } while (id == 0); return id; }
        internal void CheckAlive() { if (_disposed) throw new ObjectDisposedException(nameof(RpcContextService)); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            FailRequests(null, RpcError.Disposed, "RPC scope disposed.");
            foreach (var peer in Volatile.Read(ref _peerSnapshot)) DetachPeer(peer.Key, peer.Value);
            foreach (var id in _targets.Keys) RemoveTarget(id);
            DisposeRequests(); _awaitingMaps.Clear(); DisposeState();
        }
    }
}
