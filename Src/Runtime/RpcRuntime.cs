using System;
using System.Collections.Concurrent;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace BITKit.Multiplayer
{

public sealed partial class RpcRuntime : INetworkContext, IDisposable
{
    private sealed class Bound
    {
        public TargetKey Key = default;
        public object Instance = null!;
        public readonly object Token = new();
        public PeerId? Owner;
        public Func<AuthorizationRequest, bool>? Authorize;
        public IReadOnlyDictionary<string, (MethodInfo Body, SendTo Route, RpcDelivery Delivery)> Methods = null!;
        public IReadOnlyDictionary<ulong, TypedMethod> Typed = null!;
        public TypedTarget Numeric;
        public IReadOnlyDictionary<string, (PropertyInfo Property, MethodInfo Raw)> Properties = null!;
        public Dictionary<string, long> Versions = new();
        public readonly Dictionary<string, ulong> StateFingerprints = new();
        public readonly Dictionary<string, Action<object, object?, object?>> StateHooks = new();
        public readonly Dictionary<string, SyncCollection> Collections = new();
        public readonly Dictionary<string, MethodInfo> CollectionHooks = new();
        public bool Synchronized;
        public volatile bool Initializing;
        public volatile bool Active;
    }
    private sealed class TypedMethod
    {
        internal ulong Id, Fingerprint;
        internal string Name = "";
        internal SendTo Route;
        internal RpcDelivery Delivery;
        internal TypedRpcReceiver Receiver = null!;
        internal MethodInfo Body = null!;
    }
    private sealed class TypedFanoutLease
    {
        [ThreadStatic] private static Stack<TypedFanoutLease>? _cache;
        private RpcRuntime _runtime = null!;
        private byte[] _buffer = null!;
        private int _length, _remaining;
        internal static TypedFanoutLease Rent(RpcRuntime runtime, byte[] buffer, int length, int count)
        {
            var cache = _cache;
            var lease = cache != null && cache.Count != 0 ? cache.Pop() : new TypedFanoutLease();
            lease._runtime = runtime; lease._buffer = buffer; lease._length = length; lease._remaining = count;
            return lease;
        }
        internal void Send(PeerId peer)
        {
            try
            {
                var operation = _runtime.GetTypedOperation(peer, _buffer, _length);
                if (operation.IsCompletedSuccessfully)
                { operation.GetAwaiter().GetResult(); CompleteOne(); }
                else _ = AwaitOne(operation);
            }
            catch (Exception ex) { _runtime.ReportUnhandled(ex); CompleteOne(); }
        }
        private async Task AwaitOne(ValueTask operation)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception ex) { _runtime.ReportUnhandled(ex); }
            finally { CompleteOne(); }
        }
        internal void CompleteOne()
        {
            if (Interlocked.Decrement(ref _remaining) != 0) return;
            TypedPacketBuffer.Return(_buffer);
            _runtime.ReleaseTypedSlot();
            _buffer = null!; _runtime = null!;
            var cache = _cache ??= new Stack<TypedFanoutLease>(8);
            if (cache.Count < 64) cache.Push(this);
        }
    }
    private sealed class Waiter
    {
        public PeerId Destination;
        public TargetKey Target;
        public Type? ResultType;
        public TaskCompletionSource<byte[]?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly IRoomWire? _wire;
    private readonly IRoomMemoryWire? _memory;
    private TypedTarget _numericScope;
    private readonly Dictionary<TypedTarget, TargetKey> _numericTargets = new();
    private readonly Dictionary<ulong, PeerId> _numericPeers = new();
    private int _typedInFlight;
    private const int MaxTypedInFlight = 128;
    private readonly IRoomDatagrams? _datagrams;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private const int MaxMembers = 256, MaxTargets = 512, MaxStateEntries = 4096, MaxPending = 1024, MaxRelays = 1024, MaxRetiredPeers = 4096, MaxStateValueBytes = 16384;
    private readonly ConcurrentDictionary<TargetKey, Bound> _bindings = new();
    private readonly HashSet<TargetKey> _removedTargets = new();
    private readonly ConcurrentDictionary<TargetKey, Func<AuthorizationRequest, bool>> _relayPolicies = new();
    private readonly ConcurrentDictionary<string, Waiter> _pending = new();
    private readonly ConcurrentDictionary<string, (PeerId Requester, PeerId Destination, TargetKey Target)> _relays = new();
    private readonly ConcurrentDictionary<(string Id, PeerId Peer), TargetKey> _fanout = new();
    private readonly ConcurrentDictionary<PeerId, RoomMember> _members = new();
    private readonly HashSet<PeerId> _retiredPeers = new();
    // Optional application readiness barrier. Directory/state traffic still reaches the peer;
    // All RPCs are withheld until the peer confirms it applied its ready directory.
    private readonly HashSet<PeerId> _heldBroadcastPeers = new();
    private RoomMember[] _broadcastSnapshot = Array.Empty<RoomMember>();
    private bool _retiredPeerLimitReached;
    private readonly ConcurrentDictionary<(TargetKey, string), (long Version, byte[] Value)> _state = new();
    private readonly ConcurrentDictionary<MethodInfo, (ParameterInfo[] Parameters, Type[] Types)> _methodSchemas = new();
    private (ParameterInfo[] Parameters, Type[] Types) MethodSchema(MethodInfo method)
    {
        if (_methodSchemas.TryGetValue(method, out var schema)) return schema;
        return _methodSchemas.GetOrAdd(method, static m =>
        {
            var parameters = m.GetParameters();
            return (parameters, parameters.Where(p => p.ParameterType != typeof(RpcTarget))
                .Select(p => p.ParameterType).ToArray());
        });
    }
    private sealed class MeasureSchema
    {
        internal SendTo Route;
        internal Type[] Types = null!;
        internal int TargetIndex;
        internal WovenTypedRpcAttribute? Typed;
        internal string? OldId;
    }
    private readonly ConcurrentDictionary<MethodInfo, MeasureSchema> _measureSchemas = new();
    private MeasureSchema GetMeasureSchema(MethodInfo method)
    {
        if (_measureSchemas.TryGetValue(method, out var schema)) return schema;
        if (_measureSchemas.Count >= 1024) throw new RpcException(RpcError.LimitExceeded, "Unreliable measurement schema cache limit reached");
        return _measureSchemas.GetOrAdd(method, static m =>
        {
            var attribute = m.GetCustomAttribute<RpcAttribute>();
            if (attribute?.Delivery != RpcDelivery.Unreliable || m.ReturnType != typeof(void) ||
                !Enum.IsDefined(typeof(SendTo), attribute.To))
                throw new RpcException(RpcError.InvalidPayload, "Measurement requires an Unreliable void RPC");
            var parameters = m.GetParameters();
            if (parameters.Length > 32) throw new RpcException(RpcError.InvalidPayload, "Too many RPC measurement arguments");
            var types = parameters.Select(p => p.ParameterType).ToArray();
            int target = Array.FindIndex(types, t => t == typeof(RpcTarget));
            if (attribute.To == SendTo.Target ? target < 0 || Array.FindLastIndex(types, t => t == typeof(RpcTarget)) != target : target >= 0)
                throw new RpcException(RpcError.InvalidPayload, "Unreliable target route/parameter mismatch");
            var typed = m.GetCustomAttribute<WovenTypedRpcAttribute>();
            return new MeasureSchema { Route = attribute.To, Types = types, TargetIndex = target,
                Typed = typed, OldId = typed == null ? MethodId(m) : null };
        });
    }
    private byte[][] CallValues(MethodInfo method, object?[] args)
    {
        var (parameters, types) = MethodSchema(method);
        if (parameters.Length != args.Length) throw new RpcException(RpcError.InvalidPayload, "RPC argument count mismatch");
        var values = new byte[types.Length][]; int index = 0, total = 0;
        for (int i = 0; i < args.Length; i++)
            if (parameters[i].ParameterType != typeof(RpcTarget))
            {
                var encoded = ReliableValues.Encode(types[index], args[i]);
                if (encoded.Length > ReliableCodec.MaxFrame - total - 4096)
                    throw new RpcException(RpcError.LimitExceeded, "Reliable argument budget exceeds frame limit");
                total += encoded.Length + 4;
                values[index++] = encoded;
            }
        return values;
    }
    // Positional envelope uses full method identity, not a truncated/hash lookup. Scope and
    // original sender are authenticated by the room wire and checked again at this layer.
    private const int MaxUnreliableCandidateBytes = 65536;
    // Packet bytes are written directly into a rented array. Routing strings, boxed
    // arguments, and decoded objects may still allocate; no zero-GC claim is made here.
    private ArrayPool<byte> _unreliablePool = ArrayPool<byte>.Shared;
    private int _unreliableLeases;
    private sealed class PooledPacket : Stream, IBufferWriter<byte>
    {
        private readonly RpcRuntime _owner;
        private byte[]? _buffer;
        private int _length;
        internal PooledPacket(RpcRuntime owner)
        {
            _owner = owner;
            _buffer = owner._unreliablePool.Rent(1024);
            Interlocked.Increment(ref owner._unreliableLeases);
        }
        internal int LengthBytes => _length;
        internal ReadOnlyMemory<byte> Memory => new ReadOnlyMemory<byte>(_buffer!, 0, _length);
        internal Span<byte> GetSpanAt(int offset, int size) => _buffer!.AsSpan(offset, size);
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var size = Math.Max(sizeHint, 1);
            Reserve(size);
            return _buffer!.AsMemory(_length);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            if (count < 0 || _buffer == null || count > _buffer.Length - _length || count > MaxUnreliableCandidateBytes - _length)
                throw new RpcException(RpcError.LimitExceeded, "Invalid unreliable writer advance");
            _length += count;
        }
        private void Reserve(int bytes)
        {
            if (bytes < 0 || _length > MaxUnreliableCandidateBytes - bytes)
                throw new RpcException(RpcError.LimitExceeded, "Unreliable candidate exceeds 64 KiB measurement cap");
            var needed = _length + bytes;
            if (needed <= _buffer!.Length) return;
            var expanded = _owner._unreliablePool.Rent(Math.Min(MaxUnreliableCandidateBytes, Math.Max(needed, _buffer.Length * 2)));
            Buffer.BlockCopy(_buffer, 0, expanded, 0, _length);
            _owner._unreliablePool.Return(_buffer);
            _buffer = expanded;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
            Reserve(count);
            Buffer.BlockCopy(buffer, offset, _buffer!, _length, count);
            _length += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            buffer.CopyTo(_buffer!.AsSpan(_length));
            _length += buffer.Length;
        }
        protected override void Dispose(bool disposing)
        {
            if (_buffer != null)
            {
                _owner._unreliablePool.Return(_buffer);
                _buffer = null;
                Interlocked.Decrement(ref _owner._unreliableLeases);
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _buffer != null;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    // V4 application datagram, intentionally incompatible with the previous JSON prototype:
    // magic/version, route, seven bounded UTF8 routing strings, count, then length-prefixed
    // MemoryPack parameters. No CLR type names or truncated method hashes on the wire.
    private const byte UnreliableMagic = 0xB4, UnreliableVersion = 2;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private static void WriteByte(PooledPacket output, byte value) { var span = output.GetSpan(1); span[0] = value; output.Advance(1); }
    private static void WriteInt(PooledPacket output, int value)
    { System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(output.GetSpan(4), value); output.Advance(4); }
    private static void WriteShort(PooledPacket output, ushort value)
    { System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.GetSpan(2), value); output.Advance(2); }
    private static void WriteString(PooledPacket output, string value, int maxCharacters)
    {
        if (value.Length > maxCharacters) throw new RpcException(RpcError.InvalidPayload, "Routing field too long");
        var bytes = StrictUtf8.GetByteCount(value);
        WriteShort(output, checked((ushort)bytes));
        var written = StrictUtf8.GetBytes(value.AsSpan(), output.GetSpan(bytes));
        output.Advance(written);
    }
    private static void WriteHeader(PooledPacket output, Packet packet)
    {
        WriteByte(output, UnreliableMagic); WriteByte(output, UnreliableVersion); WriteByte(output, (byte)packet.To);
        WriteString(output, packet.Scope, 256); WriteString(output, packet.Service, 256);
        WriteString(output, packet.Entity, 256); WriteString(output, packet.Component, 256);
        WriteString(output, packet.Method, 1024); WriteString(output, packet.Destination, 128);
        WriteString(output, packet.Origin, 128);
    }
    private PooledPacket EncodeUnreliable(Packet packet, MethodInfo method, object?[] arguments)
    {
        var output = new PooledPacket(this);
        try
        {
            WriteHeader(output, packet);
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) throw new RpcException(RpcError.InvalidPayload, "Argument count mismatch");
            var count = parameters.Count(p => p.ParameterType != typeof(RpcTarget));
            if (count > 32) throw new RpcException(RpcError.InvalidPayload, "Too many unreliable arguments");
            WriteByte(output, (byte)count);
            for (int i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type == typeof(RpcTarget)) continue;
                ValidateUnreliableArgument(type, arguments[i]);
                var lengthAt = output.LengthBytes;
                WriteInt(output, 0);
                try { MemoryPackSerializer.Serialize(type, output, arguments[i]); }
                catch (Exception ex) when (ex is not RpcException)
                { throw new RpcException(RpcError.InvalidPayload, "MemoryPack serializer unavailable for " + type + ": " + ex.Message); }
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(output.GetSpanAt(lengthAt, 4), output.LengthBytes - lengthAt - 4);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }
    private PooledPacket EncodeRelayed(Packet packet, ReadOnlySpan<byte> rawArguments)
    {
        var output = new PooledPacket(this);
        try { WriteHeader(output, packet); output.Write(rawArguments); return output; }
        catch { output.Dispose(); throw; }
    }
    private ref struct UnreliableReader
    {
        private ReadOnlySpan<byte> _data;
        internal UnreliableReader(ReadOnlySpan<byte> data) => _data = data;
        internal int Remaining => _data.Length;
        internal byte Byte()
        {
            if (_data.IsEmpty) throw new RpcException(RpcError.InvalidPayload, "Truncated unreliable envelope");
            var result = _data[0]; _data = _data.Slice(1); return result;
        }
        internal int Int()
        {
            if (_data.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated unreliable envelope");
            var result = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_data);
            _data = _data.Slice(4); return result;
        }
        internal ushort Short()
        {
            if (_data.Length < 2) throw new RpcException(RpcError.InvalidPayload, "Truncated unreliable envelope");
            var result = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(_data);
            _data = _data.Slice(2); return result;
        }
        internal ReadOnlySpan<byte> Bytes(int length)
        {
            if (length < 0 || length > _data.Length) throw new RpcException(RpcError.InvalidPayload, "Invalid unreliable field length");
            var result = _data.Slice(0, length); _data = _data.Slice(length); return result;
        }
        internal string String(int maxCharacters)
        {
            var size = Short();
            if (size < 0 || size > maxCharacters * 4) throw new RpcException(RpcError.InvalidPayload, "Invalid routing field length");
            var value = StrictUtf8.GetString(Bytes(size));
            if (value.Length > maxCharacters) throw new RpcException(RpcError.InvalidPayload, "Routing field too long");
            return value;
        }
    }
    private static Packet DecodeUnreliable(ReadOnlySpan<byte> bytes, out int argumentOffset)
    {
        var reader = new UnreliableReader(bytes);
        if (reader.Byte() != UnreliableMagic || reader.Byte() != UnreliableVersion)
            throw new RpcException(RpcError.InvalidPayload, "Unsupported unreliable codec/version");
        var route = (SendTo)reader.Byte();
        var packet = new Packet { Kind = "call", To = route, Scope = reader.String(256), Service = reader.String(256),
            Entity = reader.String(256), Component = reader.String(256), Method = reader.String(1024),
            Destination = reader.String(128), Origin = reader.String(128) };
        argumentOffset = bytes.Length - reader.Remaining;
        return packet;
    }
    private static object?[] DecodeUnreliableArguments(MethodInfo method, ReadOnlySpan<byte> raw)
    {
        var reader = new UnreliableReader(raw);
        var parameters = method.GetParameters();
        var expected = parameters.Count(p => p.ParameterType != typeof(RpcTarget));
        if (expected > 32 || reader.Byte() != expected) throw new RpcException(RpcError.InvalidPayload, "Unreliable argument count mismatch");
        var values = new object?[expected]; int index = 0;
        foreach (var parameter in parameters)
        {
            var type = parameter.ParameterType;
            if (type == typeof(RpcTarget)) continue;
            var bytes = reader.Bytes(reader.Int());
            if (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ArraySegment<>))
            {
                // MemoryPack's collection header is a signed int32. Preflight before its
                // formatter can allocate an attacker-specified array from a tiny datagram.
                if (bytes.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated collection header");
                var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
                if (count < -1 || count > 256) throw new RpcException(RpcError.LimitExceeded, "Unreliable collection exceeds 256 entries");
            }
            if (type == typeof(string))
            {
                if (bytes.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated string header");
                var header = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
                // MemoryPack: -1 null; nonnegative UTF16 character count; other
                // negative values complement a UTF8 byte count, then four bytes of
                // UTF16 count. Check using long before passing it to the formatter.
                if (header >= 0 && 4L + 2L * header > bytes.Length ||
                    header < -1 && 8L + (~(long)header) > bytes.Length)
                    throw new RpcException(RpcError.InvalidPayload, "String length exceeds unreliable argument");
            }
            object? value = null;
            try
            {
                var consumed = MemoryPackSerializer.Deserialize(type, bytes, ref value);
                if (consumed != bytes.Length) throw new RpcException(RpcError.InvalidPayload, "Unreliable argument has trailing bytes");
            }
            catch (Exception ex) when (ex is not RpcException)
            { throw new RpcException(RpcError.InvalidPayload, "Invalid MemoryPack argument " + index + ": " + ex.Message); }
            ValidateUnreliableArgument(type, value);
            values[index++] = value;
        }
        if (reader.Remaining != 0) throw new RpcException(RpcError.InvalidPayload, "Trailing unreliable arguments");
        return values;
    }
    private static void ValidateUnreliableArgument(Type type, object? value)
    {
        if (type == typeof(RpcTarget)) return;
        if (value is string text && text.Length > MaxUnreliableCandidateBytes) throw new RpcException(RpcError.LimitExceeded, "Unreliable string exceeds cap");
        if (value is Array array && array.Length > 256) throw new RpcException(RpcError.LimitExceeded, "Unreliable array exceeds 256 entries");
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ArraySegment<>))
        {
            var count = (int)type.GetProperty("Count")!.GetValue(value)!;
            if (count > 256) throw new RpcException(RpcError.LimitExceeded, "Unreliable segment exceeds 256 entries");
        }
    }
    private IReadOnlyList<RoomMember> _memberSnapshot = Array.AsReadOnly(Array.Empty<RoomMember>());
    private long _memberVersion;
    private int _disposed;
    private int _hostDeparted;
    private int _unreliableInFlight;
    private const int MaxUnreliableInFlight = 128;
    private int _confirmed;
    private int _ready;
    public NetworkRole Role { get; }
    public bool IsHost => Role == NetworkRole.Host;
    public bool IsClient => Role == NetworkRole.Client;
    public PeerId LocalPeerId { get; }
    public PeerId HostPeerId { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && (!IsClient || Volatile.Read(ref _hostDeparted) == 0) && (Role == NetworkRole.Offline || _wire?.IsConnected == true);
    public bool IsReady => IsConnected && Volatile.Read(ref _ready) != 0;
    /// <summary>Caller-assigned room/session epoch. A replacement room must use a fresh scope; the transport does not allocate epochs.</summary>
    public string Scope { get; }
    public CancellationToken LifetimeCancellation => _lifetime.Token;
    private long _timeoutTicks = TimeSpan.FromSeconds(5).Ticks;
    public TimeSpan Timeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _timeoutTicks));
        set { if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive and at most ten minutes"); Interlocked.Exchange(ref _timeoutTicks, value.Ticks); }
    }
    public event Action<Exception>? UnhandledDispatch;
    public event Action<long>? MembersChanged;
    public long MemberVersion { get { lock (_gate) return _memberVersion; } }
    public IReadOnlyList<RoomMember> Members { get { lock (_gate) return _memberSnapshot; } }
    public bool TryGetMember(PeerId peer, out RoomMember? member)
    { lock (_gate) { member = _memberSnapshot.FirstOrDefault(m => m.Peer.Equals(peer)); return member != null; } }
    public bool TryGetReadyPeerByPlayerId(string playerId, out PeerId peer) => FindReady(m => m.PlayerId == playerId, playerId, out peer);
    public bool TryGetReadyPeerBySteamId(string steamId, out PeerId peer) => FindReady(m => m.SteamId == steamId, steamId, out peer);
    private bool FindReady(Func<RoomMember, bool> match, string identity, out PeerId peer)
    {
        peer = default;
        if (string.IsNullOrWhiteSpace(identity)) return false;
        lock (_gate)
        {
            var member = _memberSnapshot.FirstOrDefault(m => m.Ready && match(m));
            if (member == null) return false;
            peer = member.Peer;
            return true;
        }
    }
    // AuthorizationRequest.Method for all SyncVar snapshots and deltas, distinct from RPC wire IDs.
    public const string StateAuthorizationMethod = "$sync.state";
    // Initial snapshots notify only if the applied value differs. Subsequent deltas use the same rule.
    public event Action<TargetKey, string>? StateChanged;

    public RpcRuntime(NetworkRole role, string scope, PeerId localPeerId, PeerId hostPeerId, IRoomWire? wire = null)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 256) throw new ArgumentException("Scope must be 1..256 characters", nameof(scope));
        if (role != NetworkRole.Offline && wire == null) throw new ArgumentNullException(nameof(wire));
        if (role == NetworkRole.Host && !localPeerId.Equals(hostPeerId)) throw new ArgumentException("Host identity mismatch");
        Scope = scope; Role = role; LocalPeerId = localPeerId; HostPeerId = hostPeerId; _wire = wire;
        _memory = wire as IRoomMemoryWire; _datagrams = wire as IRoomDatagrams;
        _numericScope = TypedTarget.Scope(scope);
        _ready = role == NetworkRole.Client ? 0 : 1;
        _members[localPeerId] = new RoomMember(localPeerId);
        _memberVersion = role == NetworkRole.Client ? 0 : 1;
        RefreshMembersLocked();
        StartNetworkTime();
        if (wire != null) { if (_memory != null) _memory.MemoryReceived += ReceiveMemory; else wire.Received += Receive; wire.PeerLeft += Left; }
        if (_datagrams != null) _datagrams.UnreliableReceived += ReceiveUnreliable;
    }
    private void RefreshMembersLocked()
    {
        var snapshot = _members.Values.OrderBy(m => m.Peer.Value, StringComparer.Ordinal).ToArray();
        _numericPeers.Clear();
        foreach (var member in snapshot)
        {
            var id = TypedTarget.Peer(member.Peer);
            if (_numericPeers.TryGetValue(id, out var prior) && !prior.Equals(member.Peer))
                throw new RpcException(RpcError.InvalidPayload, "Numeric peer identity collision");
            _numericPeers[id] = member.Peer;
        }
        _memberSnapshot = Array.AsReadOnly(snapshot);
        RebuildBroadcastSnapshotLocked();
    }
    private void RebuildBroadcastSnapshotLocked() => _broadcastSnapshot = _members.Values
        .Where(m => m.Ready && !m.Peer.Equals(LocalPeerId) && !_heldBroadcastPeers.Contains(m.Peer)).ToArray();
    private TypedTarget RegisterTypedTarget(TargetKey key)
    {
        var id = TypedTarget.Hash(key);
        if (_numericTargets.TryGetValue(id, out var prior) && !prior.Equals(key))
            throw new RpcException(RpcError.InvalidPayload, "Numeric target identity collision");
        _numericTargets[id] = key;
        return id;
    }
    private void NotifyMembers(long version)
    {
        var listeners = MembersChanged;
        if (listeners == null) return;
        foreach (Action<long> listener in listeners.GetInvocationList())
            try { listener(version); } catch (Exception ex) { ReportUnhandled(ex); }
    }
    private void ReportUnhandled(Exception error)
    {
        var listeners = UnhandledDispatch;
        if (listeners == null) return;
        foreach (Action<Exception> listener in listeners.GetInvocationList())
            try { listener(error); } catch { /* An observer cannot tear down a socket receive callback. */ }
    }
    private void NotifyState(TargetKey key, string property)
    {
        var listeners = StateChanged;
        if (listeners == null) return;
        foreach (Action<TargetKey, string> listener in listeners.GetInvocationList())
            try { listener(key, property); } catch (Exception ex) { ReportUnhandled(ex); }
    }
    private static void ValidateMember(RoomMember member)
    {
        if (member == null || string.IsNullOrWhiteSpace(member.Peer.Value) || member.Peer.Value.Length > 128 ||
            member.PlayerId != null && (string.IsNullOrWhiteSpace(member.PlayerId) || member.PlayerId.Length > 256) ||
            member.SteamId != null && (string.IsNullOrWhiteSpace(member.SteamId) || member.SteamId.Length > 256))
            throw new RpcException(RpcError.InvalidPayload, "Invalid member identity");
    }
    private void ValidateDirectoryLocked(RoomMember member)
    {
        ValidateMember(member);
        var numeric = TypedTarget.Peer(member.Peer);
        if (_numericPeers.TryGetValue(numeric, out var priorPeer) && !priorPeer.Equals(member.Peer))
            throw new RpcException(RpcError.InvalidPayload, "Numeric peer identity collision");
        if (member.Peer.Equals(LocalPeerId) && !member.Ready) throw new RpcException(RpcError.InvalidPayload, "Host must remain ready");
        if (_members.TryGetValue(member.Peer, out var existing) &&
            (existing.PlayerId != null && existing.PlayerId != member.PlayerId || existing.SteamId != null && existing.SteamId != member.SteamId))
            throw new RpcException(RpcError.Unauthorized, "Member identity changed without a new peer session");
        if (!_members.ContainsKey(member.Peer) && _members.Count >= MaxMembers) throw new RpcException(RpcError.LimitExceeded, "Room member limit reached");
        if (!_members.ContainsKey(member.Peer) && _retiredPeers.Contains(member.Peer))
            throw new RpcException(RpcError.Disposed, "Peer ID retired in this room epoch; assign a fresh session ID");
        if (!_members.ContainsKey(member.Peer) && _retiredPeerLimitReached)
            throw new RpcException(RpcError.LimitExceeded, "Room peer epoch history exhausted; open a new room scope");
        if (_members.Values.Any(m => !m.Peer.Equals(member.Peer) &&
            (member.PlayerId != null && member.PlayerId == m.PlayerId || member.SteamId != null && member.SteamId == m.SteamId)))
            throw new RpcException(RpcError.Unauthorized, "Duplicate player or Steam identity");
    }
    private Packet MemberPacketLocked() => new()
    {
        Kind = "members", Version = _memberVersion,
        Members = _memberSnapshot.Select(m => new MemberRecord { Peer = m.Peer.Value, PlayerId = m.PlayerId, SteamId = m.SteamId, Ready = m.Ready }).ToList(),
        RemovedTargets = _removedTargets.Select(t => new TargetRecord { Service = t.Service, Entity = t.Entity, Component = t.Component }).ToList()
    };
    private void PublishMembers(Packet packet, PeerId[] peers)
    { foreach (var peer in peers) SendBackground(peer, packet.Copy()); NotifyMembers(packet.Version); }
    private void UpdateClientReadinessLocked()
    {
        if (IsClient) _ready = _confirmed != 0 && _memberVersion > 0 &&
            _members.TryGetValue(LocalPeerId, out var member) && member.Ready ? 1 : 0;
    }
    private Waiter[] RemoveAllPendingLocked()
    {
        var removed = new List<Waiter>();
        foreach (var entry in _pending.ToArray())
            if (_pending.TryRemove(entry.Key, out var waiter)) removed.Add(waiter);
        return removed.ToArray();
    }
    private void RequestBoundSnapshots(TargetKey[] keys)
    { foreach (var key in keys) SendBackground(HostPeerId, new Packet { Kind = "snapshotRequest", Service = key.Service, Entity = key.Entity, Component = key.Component }); }
    public void RegisterMember(RoomMember member)
    {
        Packet directory;
        PeerId[] recipients;
        (TargetKey Key, string Property, long Version, byte[] Value)[] state;
        lock (_gate)
        {
            Check(); RequireHost(); ValidateDirectoryLocked(member);
            if (_members.TryGetValue(member.Peer, out var prior) && prior.Ready == member.Ready &&
                prior.PlayerId == member.PlayerId && prior.SteamId == member.SteamId) return;
            _members[member.Peer] = member;
            if (_memberVersion == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Member directory version exhausted");
            _memberVersion++;
            RefreshMembersLocked();
            directory = MemberPacketLocked();
            recipients = ReadyPeersLocked();
            // The affected member must receive its own demotion, even though it is no longer ready.
            if (!member.Peer.Equals(LocalPeerId) && !member.Ready && !recipients.Contains(member.Peer))
                recipients = recipients.Concat(new[] { member.Peer }).ToArray();
            state = member.Ready && !member.Peer.Equals(LocalPeerId)
                ? _state.ToArray().Select(e => (e.Key.Item1, e.Key.Item2, e.Value.Version, e.Value.Value)).ToArray()
                : Array.Empty<(TargetKey, string, long, byte[])>();
        }
        PublishMembers(directory, recipients);
        foreach (var entry in state) if (CanReceiveState(member, entry.Key)) SendBackground(member.Peer, StatePacket(entry.Key, entry.Property, entry.Version, entry.Value));
        if (member.Ready) SendCollectionSnapshots(member);
    }
    private PeerId[] ReadyPeersLocked() => _members.Values.Where(m => m.Ready && !m.Peer.Equals(LocalPeerId)).Select(m => m.Peer).ToArray();
    public void HoldBroadcasts(PeerId peer)
    {
        lock (_gate) { Check(); RequireHost(); if (peer.Equals(LocalPeerId)) throw new ArgumentException("Cannot hold Host broadcasts", nameof(peer)); _heldBroadcastPeers.Add(peer); RebuildBroadcastSnapshotLocked(); }
    }
    public void ReleaseBroadcasts(PeerId peer)
    {
        lock (_gate)
        {
            Check(); RequireHost();
            if (!_members.TryGetValue(peer, out var member) || !member.Ready) throw new RpcException(RpcError.Unauthorized, "Peer is not ready");
            _heldBroadcastPeers.Remove(peer);
            RebuildBroadcastSnapshotLocked();
        }
    }
    // Called after the application's authenticated admission/ready acknowledgment; Host still enforces its own directory.
    public void ConfirmReady()
    {
        TargetKey[] keys;
        lock (_gate)
        {
            Check();
            if (!IsClient) throw new RpcException(RpcError.InvalidRole, "Only clients confirm admission");
            _confirmed = 1;
            UpdateClientReadinessLocked();
            keys = _bindings.Keys.ToArray();
        }
        RequestMembers();
        if (IsReady) RequestBoundSnapshots(keys);
    }
    // Explicit idempotent resynchronization for a dropped/failed directory notification.
    public void RequestMembers()
    {
        Check();
        if (!IsClient || Volatile.Read(ref _confirmed) == 0) throw new RpcException(RpcError.InvalidRole, "Confirmed Client required");
        SendBackground(HostPeerId, new Packet { Kind = "membersRequest" });
    }
    public void RemoveMember(PeerId peer)
    {
        lock (_gate) { Check(); RequireHost(); if (peer.Equals(LocalPeerId)) throw new RpcException(RpcError.InvalidRole, "Host cannot leave its directory"); }
        Leave(peer, true);
    }
    // A peer-only service may have no implementation at the Host; policy remains authority-owned.
    public void RegisterRelayPolicy(TargetKey key, Func<AuthorizationRequest, bool> authorize)
    {
        lock (_gate)
        {
            Check(); RequireHost();
            if (_removedTargets.Contains(key)) throw new RpcException(RpcError.Disposed, "Target removed");
            if (_relayPolicies.Count >= MaxTargets) throw new RpcException(RpcError.LimitExceeded, "Relay policy limit reached");
            RegisterTypedTarget(key);
            if (!_relayPolicies.TryAdd(key, authorize ?? throw new ArgumentNullException(nameof(authorize))))
                throw new InvalidOperationException("Duplicate relay policy: " + key);
        }
    }
    public void UnregisterRelayPolicy(TargetKey key) { lock (_gate) _relayPolicies.TryRemove(key, out _); }
    public void Bind(TargetKey key, object instance, Func<AuthorizationRequest, bool>? authorize = null, PeerId? owner = null)
    {
        Check();
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        // Service calls require explicit authorization; entity calls require an owner or explicit authorization.
        if (IsHost && authorize == null && owner == null) throw new ArgumentException("Explicit authorization or owner required");
        var bound = new Bound { Key = key, Instance = instance, Authorize = authorize, Owner = owner, Initializing = true };
        var methods = new Dictionary<string, (MethodInfo Body, SendTo Route, RpcDelivery Delivery)>();
        var typed = new Dictionary<ulong, TypedMethod>();
        var properties = new Dictionary<string, (PropertyInfo Property, MethodInfo Raw)>();
        var implementation = instance.GetType();
        var declared = new Dictionary<MethodInfo, (string Id, MethodInfo Body, SendTo Route, RpcDelivery Delivery)>();
        for (var current = implementation; current != null; current = current.BaseType)
        foreach (var method in current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attribute = method.GetCustomAttribute<RpcAttribute>();
            if (attribute == null) continue;
            var id = method.GetCustomAttribute<WovenRpcAttribute>()?.Id ?? throw new InvalidOperationException("Method was not woven: " + method);
            var body = current.GetMethod("__bitkit_body_" + method.Name + "_" + StableHash(id), BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (body == null) throw new InvalidOperationException("Missing woven body: " + method);
            if (!Enum.IsDefined(typeof(RpcDelivery), attribute.Delivery) || attribute.Delivery == RpcDelivery.Unreliable && method.ReturnType != typeof(void))
                throw new InvalidOperationException("Invalid RPC delivery: " + method);
            if (!methods.TryAdd(id, (body, attribute.To, attribute.Delivery))) throw new InvalidOperationException("Duplicate RPC: " + id);
            if (method.GetCustomAttribute<WovenTypedRpcAttribute>() is { } marker)
            {
                var entry = current.GetMethod("__bitkit_recv_" + method.Name + "_" + StableHash(id), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? throw new InvalidOperationException("Missing typed RPC receiver: " + method);
                var receiver = (TypedRpcReceiver)Delegate.CreateDelegate(typeof(TypedRpcReceiver), entry);
                if (typed.TryGetValue(marker.Method, out var previous) && previous.Fingerprint != marker.Fingerprint)
                    throw new InvalidOperationException("Numeric method ID collision: " + id);
                typed.Add(marker.Method, new TypedMethod { Id = marker.Method, Fingerprint = marker.Fingerprint,
                    Body = body, Name = id, Route = attribute.To, Delivery = attribute.Delivery, Receiver = receiver });
            }
            foreach (var type in MethodSchema(body).Types) if (attribute.Delivery == RpcDelivery.Reliable) ReliableValues.Check(type);
            if (attribute.Delivery == RpcDelivery.Reliable && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                ReliableValues.Check(method.ReturnType.GetGenericArguments()[0]);
            declared.Add(method, (id, body, attribute.To, attribute.Delivery));
        }
        foreach (var contract in implementation.GetInterfaces())
        {
            var map = implementation.GetInterfaceMap(contract);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (!declared.TryGetValue(map.TargetMethods[i], out var target)) continue;
                var alias = MethodId(map.InterfaceMethods[i]);
                if (methods.TryGetValue(alias, out var existing) && existing.Body != target.Body)
                    throw new InvalidOperationException("Conflicting interface RPC alias: " + alias);
                methods[alias] = (target.Body, target.Route, target.Delivery);
            }
        }
        for (var current = implementation; current != null; current = current.BaseType)
        foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetCustomAttribute<SyncVarAttribute>() == null) continue;
            var fingerprint = property.GetCustomAttribute<WovenSyncVarAttribute>()?.Fingerprint
                ?? throw new InvalidOperationException("SyncVar schema was not woven: " + property.Name);
            if (!bound.StateFingerprints.TryAdd(property.Name, fingerprint)) throw new InvalidOperationException("Inherited SyncVar name collision: " + property.Name);
            var hook = current.GetMethod("__bitkit_hook_" + property.Name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (typeof(SyncCollection).IsAssignableFrom(property.PropertyType))
            {
                var collection = property.GetValue(instance) as SyncCollection ?? throw new InvalidOperationException("SyncVar collection must be explicitly initialized: " + property.Name);
                if (bound.Collections.Values.Contains(collection)) throw new InvalidOperationException("A collection instance cannot serve two SyncVar members");
                bound.Collections.Add(property.Name, collection);
                if (hook != null) bound.CollectionHooks.Add(property.Name, hook);
                continue;
            }
            if (hook != null) bound.StateHooks.Add(property.Name, (Action<object, object?, object?>)Delegate.CreateDelegate(typeof(Action<object, object?, object?>), hook));
            var raw = current.GetMethod("__bitkit_set_" + property.Name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new InvalidOperationException("SyncVar not woven: " + property.Name);
            if (!properties.TryAdd(property.Name, (property, raw))) throw new InvalidOperationException("Inherited SyncVar name collision: " + property.Name);
            ReliableValues.CheckStateSchema(property.PropertyType);
        }
        var stateContract = SyncWire.ContractFingerprint(bound.StateFingerprints);
        foreach (var name in bound.StateFingerprints.Keys.ToArray())
            bound.StateFingerprints[name] = SyncWire.Mix(bound.StateFingerprints[name], stateContract);
        bound.Methods = new ReadOnlyDictionary<string, (MethodInfo Body, SendTo Route, RpcDelivery Delivery)>(methods);
        bound.Typed = new ReadOnlyDictionary<ulong, TypedMethod>(typed);
        bound.Properties = new ReadOnlyDictionary<string, (PropertyInfo Property, MethodInfo Raw)>(properties);
        var initial = new Dictionary<string, byte[]>();
        if (IsHost)
            foreach (var entry in bound.Properties)
            {
                var value = entry.Value.Property.GetValue(instance);
                var token = EncodeState(bound, entry.Key, value);
                initial.Add(entry.Key, token);
            }
        var restore = new List<(string Property, long Version, byte[] Value)>();
        lock (_gate)
        {
            Check();
            if (_removedTargets.Contains(key)) throw new RpcException(RpcError.Disposed, "Target identity permanently removed; use a new key or scope");
            if (_bindings.ContainsKey(key)) throw new InvalidOperationException("Key already bound: " + key);
            if (_bindings.Count >= MaxTargets) throw new RpcException(RpcError.LimitExceeded, "Bound target limit reached");
            bound.Numeric = RegisterTypedTarget(key);
            if (StateEntryCountLocked() + bound.StateFingerprints.Keys.Count(p => !_state.ContainsKey((key, p))) > MaxStateEntries ||
                _bindings.Values.Sum(b => b.StateFingerprints.Count) + bound.StateFingerprints.Count > MaxStateEntries)
                throw new RpcException(RpcError.LimitExceeded, "Synchronized state limit reached");
            foreach (var collection in bound.Collections.Values) lock (collection.Gate)
                if (collection.Binding != null) throw new InvalidOperationException("Collection already bound");
            if (!DispatchBridge.Attach(instance, this, key, bound.Token)) throw new InvalidOperationException("Object already bound");
            bound.Active = true;
            _bindings[key] = bound;
            try { AttachCollections(bound); }
            catch { DetachCollections(bound, false); bound.Active = false; _bindings.TryRemove(key, out _); DispatchBridge.Detach(instance, this); throw; }
            foreach (var property in bound.Properties.Keys)
            {
                if (_state.TryGetValue((key, property), out var value)) restore.Add((property, value.Version, value.Value));
                else if (IsHost) { _state[(key, property)] = (0, initial[property]); bound.Versions[property] = 0; }
            }
            foreach (var property in bound.Collections.Keys)
                if (_state.TryGetValue((key, property), out var value)) restore.Add((property, value.Version, value.Value));
        }
        try
        {
            foreach (var entry in restore)
                if (bound.Collections.ContainsKey(entry.Property)) ReceiveCollection(StatePacket(key, entry.Property, entry.Version, entry.Value), false);
                else Apply(bound, entry.Property, entry.Version, entry.Value);
        }
        catch
        {
            lock (_gate)
                if (_bindings.TryGetValue(key, out var current) && ReferenceEquals(bound, current))
                { _bindings.TryRemove(key, out _); bound.Active = false; DetachCollections(bound, false); DispatchBridge.Detach(instance, this); }
            throw;
        }
        lock (_gate)
        {
            if (_disposed != 0 || !bound.Active || !_bindings.TryGetValue(key, out var current) || !ReferenceEquals(current, bound))
                throw new RpcException(RpcError.Disposed, "Target unbound during initialization");
            bound.Initializing = false;
        }
        if (IsHost)
            foreach (var member in _members.Values.Where(m => CanReceiveState(m, key)))
                foreach (var entry in bound.Properties.Keys)
                    if (_state.TryGetValue((key, entry), out var value)) SendBackground(member.Peer, StatePacket(key, entry, value.Version, value.Value));
        if (IsClient && IsReady) SendBackground(HostPeerId, new Packet { Kind = "snapshotRequest", Service = key.Service, Entity = key.Entity, Component = key.Component });
        if (IsHost) foreach (var member in _members.Values) SendCollectionSnapshots(member, key);
        NotifySynchronized(bound);
    }
    public void Unbind(TargetKey key)
    {
        Waiter[] pending;
        int lostFanout;
        (PeerId Requester, string Id)[] lostRelays;
        lock (_gate)
        {
            if (_bindings.TryRemove(key, out var bound)) { bound.Active = false; DetachCollections(bound, true); DispatchBridge.Detach(bound.Instance, this); }
            pending = RemovePendingForTargetLocked(key);
            lostFanout = RemoveFanoutForTargetLocked(key);
            lostRelays = RemoveRelaysForTargetLocked(key);
        }
        NotifyTargetUnbound(key);
        foreach (var waiter in pending) waiter.Completion.TrySetException(new RpcException(RpcError.Disposed, "Target unbound"));
        if (lostFanout > 0) ReportUnhandled(new RpcException(RpcError.Disposed, "All target unbound during delivery"));
        foreach (var relay in lostRelays) SendBackground(relay.Requester, new Packet { Kind = "reply", Id = relay.Id, Origin = LocalPeerId.Value, Error = RpcError.Disposed, Fault = "Target unbound" });
    }
    // Unbind retains state for the SAME logical identity. RemoveTarget permanently tombstones it for this scope.
    public void RemoveTarget(TargetKey key)
    {
        // Removal has no SyncVar prerequisite: RPC-only targets and temporarily unready
        // admitted peers must also discard this identity before a later ready transition.
        PeerId[] recipients;
        Waiter[] pending;
        int lostFanout;
        (PeerId Requester, string Id)[] lostRelays;
        bool firstRemoval;
        Packet? directory = null;
        PeerId[] directoryRecipients = Array.Empty<PeerId>();
        lock (_gate)
        {
            Check();
            recipients = IsHost ? _members.Keys.Where(peer => !peer.Equals(LocalPeerId)).ToArray() : Array.Empty<PeerId>();
            if (!_removedTargets.Contains(key) && _removedTargets.Count >= MaxTargets) throw new RpcException(RpcError.LimitExceeded, "Removed target limit reached");
            if (IsHost && !_removedTargets.Contains(key) && _memberVersion == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Member directory version exhausted");
            firstRemoval = _removedTargets.Add(key);
            if (_bindings.TryRemove(key, out var bound)) { bound.Active = false; DetachCollections(bound, false); DispatchBridge.Detach(bound.Instance, this); }
            foreach (var stateKey in _state.Keys.Where(k => k.Item1.Equals(key)).ToArray()) _state.TryRemove(stateKey, out _);
            _relayPolicies.TryRemove(key, out _);
            pending = RemovePendingForTargetLocked(key);
            lostFanout = RemoveFanoutForTargetLocked(key);
            lostRelays = RemoveRelaysForTargetLocked(key);
            if (firstRemoval && IsHost)
            {
                _memberVersion++;
                directory = MemberPacketLocked();
                directoryRecipients = ReadyPeersLocked();
            }
        }
        if (firstRemoval) NotifyTargetUnbound(key);
        foreach (var waiter in pending) waiter.Completion.TrySetException(new RpcException(RpcError.Disposed, "Target removed"));
        if (lostFanout > 0) ReportUnhandled(new RpcException(RpcError.Disposed, "All target removed during delivery"));
        foreach (var relay in lostRelays) SendBackground(relay.Requester, new Packet { Kind = "reply", Id = relay.Id, Origin = LocalPeerId.Value, Error = RpcError.Disposed, Fault = "Target removed" });
        if (firstRemoval)
            foreach (var peer in recipients) SendBackground(peer, new Packet { Kind = "targetRemoved", Service = key.Service, Entity = key.Entity, Component = key.Component });
        if (directory != null) PublishMembers(directory, directoryRecipients);
    }
    private Waiter[] RemovePendingForTargetLocked(TargetKey key)
    {
        var removed = new List<Waiter>();
        foreach (var entry in _pending.ToArray())
            if (entry.Value.Target.Equals(key) && _pending.TryRemove(entry.Key, out var waiter)) removed.Add(waiter);
        return removed.ToArray();
    }
    private int RemoveFanoutForTargetLocked(TargetKey key)
    {
        int removed = 0;
        foreach (var entry in _fanout.ToArray())
            if (entry.Value.Equals(key) && _fanout.TryRemove(entry.Key, out _)) removed++;
        return removed;
    }
    private (PeerId Requester, string Id)[] RemoveRelaysForTargetLocked(TargetKey key)
    {
        var removed = new List<(PeerId, string)>();
        foreach (var entry in _relays.ToArray())
            if (entry.Value.Target.Equals(key) && _relays.TryRemove(entry.Key, out var relay)) removed.Add((relay.Requester, entry.Key));
        return removed.ToArray();
    }
    public T CreateProxy<T>(TargetKey key, IReadOnlyDictionary<string, SendTo> routes) where T : class
        => CreateProxyWithDelivery<T>(key, routes, new Dictionary<string, RpcDelivery>());
    public T CreateProxyWithDelivery<T>(TargetKey key, IReadOnlyDictionary<string, SendTo> routes, IReadOnlyDictionary<string, RpcDelivery> deliveries) where T : class
    {
        Check();
        if (!typeof(T).IsInterface) throw new ArgumentException("Only interfaces can be proxied");
        var proxy = System.Reflection.DispatchProxy.Create<T, RemoteProxy<T>>();
        ((RemoteProxy<T>)(object)proxy).Initialize(this, key, routes, deliveries);
        return proxy;
    }
    internal object? Dispatch(object instance, TargetKey key, object token, string id, object?[] args, SendTo to, Type resultType)
    {
        lock (_gate)
            if (!_bindings.TryGetValue(key, out var bound) || !bound.Active || bound.Initializing || !ReferenceEquals(bound.Instance, instance) || !ReferenceEquals(bound.Token, token))
                throw new RpcException(RpcError.Disposed, "Binding changed during invocation");
        return DispatchCore(key, id, args, to, resultType, instance);
    }
    internal object? DispatchProxy(TargetKey key, MethodInfo method, object?[] args, SendTo to, RpcDelivery? delivery)
        => DispatchCore(key, MethodId(method), args, to, method.ReturnType, method, delivery);
    public Task<T> CallAsync<T>(TargetKey key, MethodInfo method, SendTo to, object?[] args, CancellationToken cancellationToken = default)
    {
        if (method.GetCustomAttribute<RpcAttribute>()?.Delivery == RpcDelivery.Unreliable ||
            _bindings.TryGetValue(key, out var local) && local.Methods.TryGetValue(MethodId(method), out var registered) && registered.Delivery == RpcDelivery.Unreliable)
            throw new RpcException(RpcError.InvalidPayload, "Unreliable RPC cannot return a result");
        if (method.ReturnType != typeof(Task<T>)) throw new ArgumentException("Method must return Task<T>", nameof(method));
        if (to == SendTo.All) throw new RpcException(RpcError.InvalidRole, "All cannot return Task<T>");
        Check();
        lock (_gate) if (_removedTargets.Contains(key)) throw new RpcException(RpcError.Disposed, "Target removed");
        if (!IsReady) throw new RpcException(RpcError.Unauthorized, "Client is not admitted/ready");
        if (Role == NetworkRole.Offline) throw new RpcException(RpcError.InvalidRole, "Invalid caller role");
        var peer = to == SendTo.Target ? args.OfType<RpcTarget>().Single().Peer : HostPeerId;
        var packet = new Packet { Kind = "call", Id = Guid.NewGuid().ToString("N"), Service = key.Service, Entity = key.Entity, Component = key.Component,
            Method = MethodId(method), To = to, Destination = peer.Value,
            ExpectedType = typeof(T), Args = CallValues(method, args) };
        return ConvertResult<T>(InvokeOutgoing(packet, false, cancellationToken));
    }
    private object? DispatchCore(TargetKey key, string id, object?[] args, SendTo to, Type returnType, object? instance, RpcDelivery? requestedDelivery = null)
    {
        Check();
        lock (_gate) if (_removedTargets.Contains(key)) throw new RpcException(RpcError.Disposed, "Target removed");
        if (!IsReady) throw new RpcException(RpcError.Unauthorized, "Client is not admitted/ready");
        if (Role == NetworkRole.Offline) throw new RpcException(RpcError.InvalidRole, "No network in Offline role");
        if (requestedDelivery.HasValue && !Enum.IsDefined(typeof(RpcDelivery), requestedDelivery.Value)) throw new RpcException(RpcError.InvalidPayload, "Unknown delivery");
        var delivery = ResolveDelivery(key, id, instance);
        if (requestedDelivery.HasValue && _bindings.TryGetValue(key, out var known) && known.Methods.TryGetValue(id, out var methodDef) && methodDef.Delivery != requestedDelivery.Value)
            throw new RpcException(RpcError.InvalidPayload, "Proxy delivery disagrees with declaration");
        if ((requestedDelivery ?? delivery) == RpcDelivery.Unreliable)
        {
            if (returnType != typeof(void)) throw new RpcException(RpcError.InvalidPayload, "Unreliable RPC requires void");
            SendUnreliableCall(key, id, args, to, instance as MethodInfo);
            return null;
        }
        if (to == SendTo.All && !IsHost) throw new RpcException(RpcError.InvalidRole, "Only Host may send All");
        if (to == SendTo.All && returnType != typeof(void))
            throw new RpcException(RpcError.InvalidPayload, "All requires void");
        if (returnType != typeof(void) && returnType != typeof(Task) && !(returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
            throw new RpcException(RpcError.InvalidPayload, "Unsupported return shape");
        var peer = to == SendTo.Target ? args.OfType<RpcTarget>().SingleOrDefault().Peer : HostPeerId;
        if (to == SendTo.Target && string.IsNullOrEmpty(peer.Value)) throw new RpcException(RpcError.InvalidPayload, "Target parameter required");
        var method = _bindings.TryGetValue(key, out var binding) && binding.Active && binding.Methods.TryGetValue(id, out var declaration)
            ? declaration.Body : instance as MethodInfo ?? throw new RpcException(RpcError.MissingMethod, "Reliable method schema unavailable");
        var packet = new Packet { Kind = "call", Id = Guid.NewGuid().ToString("N"), Service = key.Service, Entity = key.Entity, Component = key.Component,
            Method = id, Args = CallValues(method, args), To = to, Destination = peer.Value,
            ExpectedType = returnType.IsGenericType ? returnType.GenericTypeArguments[0] : null };
        if (returnType == typeof(void)) { packet.OneWay = true; SendReliableVoid(packet); return null; }
        var task = InvokeOutgoing(packet, false);
        if (returnType == typeof(Task)) return task;
        var result = returnType.GenericTypeArguments[0];
        return typeof(RpcRuntime).GetMethod(nameof(ConvertResult), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(result).Invoke(null, new object[] { task })!;
    }
    private async Task Observe(Task<byte[]?> task) { try { await task.ConfigureAwait(false); } catch (Exception ex) { ReportUnhandled(ex); } }
    private void SendReliableVoid(Packet packet)
    {
        if (packet.To == SendTo.All)
        {
            GetDeclaredMethod(packet);
            var local = Execute(packet, LocalPeerId);
            if (local.IsCompleted) local.GetAwaiter().GetResult();
            else _ = Observe(local);
            PeerId[] recipients;
            lock (_gate) recipients = ReadyPeersLocked().Where(p => !_heldBroadcastPeers.Contains(p)).ToArray();
            foreach (var peer in recipients)
            {
                var copy = packet.Copy(); copy.Destination = peer.Value;
                SendBackground(peer, copy);
            }
            return;
        }
        var destination = new PeerId(packet.Destination);
        if (destination.Equals(LocalPeerId))
        {
            var local = Execute(packet, LocalPeerId);
            if (local.IsCompleted) local.GetAwaiter().GetResult(); else _ = Observe(local);
            return;
        }
        var next = IsClient ? HostPeerId : destination;
        if (IsHost && (!_members.TryGetValue(next, out var member) || !member.Ready))
            throw new RpcException(RpcError.MissingTarget, "Peer not ready");
        SendBackground(next, packet); // failures are local diagnostics, not remote completion promises.
    }
    private static async Task<T> ConvertResult<T>(Task<byte[]?> task)
    {
        var value = await task.ConfigureAwait(false);
        return (T)ReliableValues.Decode(typeof(T), value)!;
    }
    private async Task<byte[]?> InvokeOutgoing(Packet packet, bool noWait, CancellationToken cancellationToken = default)
    {
        if (packet.To == SendTo.All)
        {
            // Refuse a forged proxy route before broadcasting to any other peer.
            GetDeclaredMethod(packet);
            PeerId[] recipients;
            lock (_gate)
            {
                Check();
                recipients = ReadyPeersLocked().Where(p => !_heldBroadcastPeers.Contains(p)).ToArray();
                if (_fanout.Count + recipients.Length > MaxPending) throw new RpcException(RpcError.LimitExceeded, "Fanout acknowledgement limit reached");
                foreach (var peer in recipients) _fanout[(packet.Id, peer)] = packet.Key;
            }
            var local = WaitFor(packet, LocalPeerId, () => Execute(packet, LocalPeerId), true, cancellationToken);
            if (local.IsFaulted || local.IsCanceled)
            {
                foreach (var peer in recipients) _fanout.TryRemove((packet.Id, peer), out _);
                return await local.ConfigureAwait(false);
            }
            foreach (var peer in recipients)
            {
                var copy = packet.Copy(); copy.Destination = peer.Value;
                _ = ExpireFanout(packet.Id, peer);
                SendBackground(peer, copy);
            }
            return await local.ConfigureAwait(false);
        }
        var destination = new PeerId(packet.Destination);
        if (destination.Equals(LocalPeerId))
            return await WaitFor(packet, LocalPeerId, () => Execute(packet, LocalPeerId), true, cancellationToken).ConfigureAwait(false);
        var next = IsClient ? HostPeerId : destination;
        if (IsHost && (!_members.TryGetValue(next, out var m) || !m.Ready)) throw new RpcException(RpcError.MissingTarget, "Peer not ready");
        // Void keeps its source signature but still waits internally for the correlated reply.
        return await WaitFor(packet, next, async () => { await Send(next, packet).ConfigureAwait(false); return null; }, false, cancellationToken).ConfigureAwait(false);
    }
    private async Task<byte[]?> WaitFor(Packet packet, PeerId destination, Func<Task<byte[]?>> start, bool completeOnSuccess, CancellationToken cancellationToken)
    {
        var waiter = new Waiter { Destination = destination, Target = packet.Key, ResultType = packet.ExpectedType };
        lock (_gate)
        {
            Check();
            if (_removedTargets.Contains(packet.Key)) throw new RpcException(RpcError.Disposed, "Target removed");
            if (_pending.Count >= MaxPending) throw new RpcException(RpcError.LimitExceeded, "Pending request limit reached");
            if (!_pending.TryAdd(packet.Id, waiter)) throw new InvalidOperationException("Duplicate request ID");
        }
        try
        {
            Check(); // Close the race with Dispose between outgoing validation and pending registration.
            using var timeout = new CancellationTokenSource(Timeout);
            using var registration = timeout.Token.Register(() => waiter.Completion.TrySetException(new RpcException(RpcError.Timeout, "Result unknown; request may have executed")));
            using var cancellation = cancellationToken.Register(() => waiter.Completion.TrySetCanceled(cancellationToken));
            if (!waiter.Completion.Task.IsCompleted)
            {
                try { _ = ObserveOperation(start(), waiter, completeOnSuccess); }
                catch (Exception ex) { waiter.Completion.TrySetException(ex); }
            }
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(packet.Id, out _); }
    }
    private static async Task ObserveOperation(Task<byte[]?> operation, Waiter waiter, bool completeOnSuccess)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (completeOnSuccess) waiter.Completion.TrySetResult(result);
        }
        catch (Exception ex) { waiter.Completion.TrySetException(ex); }
    }
    private (MethodInfo Body, SendTo Route, RpcDelivery Delivery) GetDeclaredMethod(Packet packet)
    {
        if (!_bindings.TryGetValue(packet.Key, out var binding) || !binding.Active || binding.Initializing) throw new RpcException(RpcError.MissingTarget, "Missing bound target " + packet.Key);
        if (!binding.Methods.TryGetValue(packet.Method, out var declaration)) throw new RpcException(RpcError.MissingMethod, "Missing method " + packet.Method);
        if (declaration.Route != packet.To) throw new RpcException(RpcError.InvalidPayload, "RPC route disagrees with the bound declaration");
        return declaration;
    }
    private async Task<byte[]?> Execute(Packet packet, PeerId sender, RpcDelivery arrival = RpcDelivery.Reliable)
    {
        Bound binding;
        lock (_gate)
        {
            if (_disposed != 0 || _removedTargets.Contains(packet.Key)) throw new RpcException(RpcError.Disposed, "Target removed or runtime disposed");
            if (!_bindings.TryGetValue(packet.Key, out binding!) || !binding.Active || binding.Initializing) throw new RpcException(RpcError.MissingTarget, "Missing bound target " + packet.Key);
        }
        var declaration = GetDeclaredMethod(packet);
        if (declaration.Delivery != arrival) throw new RpcException(RpcError.InvalidPayload, "RPC arrival lane disagrees with declaration");
        if (packet.To == SendTo.Host && !IsHost || packet.To == SendTo.All && !(IsHost ? sender.Equals(LocalPeerId) : sender.Equals(HostPeerId)) ||
            packet.To == SendTo.Target && packet.Destination != LocalPeerId.Value)
            throw new RpcException(RpcError.InvalidRole, "RPC delivered to the wrong role or target");
        var method = declaration.Body;
        if (IsClient && !IsReady) throw new RpcException(RpcError.Unauthorized, "Local member is not ready");
        if (IsHost)
        {
            if (!_members.TryGetValue(sender, out var member) || !member.Ready ||
                (binding.Owner.HasValue && !sender.Equals(LocalPeerId) && !binding.Owner.Value.Equals(sender)) ||
                (binding.Authorize != null && !binding.Authorize(new AuthorizationRequest(member, LocalPeerId, binding.Key, packet.Method))))
                throw new RpcException(RpcError.Unauthorized, "Denied by Host");
        }
        else if (binding.Authorize != null)
        {
            if (!_members.TryGetValue(sender, out var member) || !member.Ready)
                throw new RpcException(RpcError.Unauthorized, "Sender absent from ready directory");
            if (!binding.Authorize(new AuthorizationRequest(member, LocalPeerId, binding.Key, packet.Method))) throw new RpcException(RpcError.Unauthorized, "Denied by receiver");
        }
        return await RpcCallContext.Run(new RpcCallContext(sender, LocalPeerId, binding.Key), async () =>
        {
            var parameters = MethodSchema(method).Parameters;
            var args = new object?[parameters.Length]; int position = 0;
            for (int i = 0; i < args.Length; i++)
            {
                try
                {
                    args[i] = parameters[i].ParameterType == typeof(RpcTarget) ? new RpcTarget(new PeerId(packet.Destination)) :
                        arrival == RpcDelivery.Unreliable
                            ? packet.BinaryArgs != null && position < packet.BinaryArgs.Length ? packet.BinaryArgs[position++] : throw new RpcException(RpcError.InvalidPayload, "Too few binary arguments")
                            : packet.BinaryArgs != null ? position < packet.BinaryArgs.Length ? packet.BinaryArgs[position++] : throw new RpcException(RpcError.InvalidPayload, "Too few local arguments")
                            : packet.Args != null && position < packet.Args.Length ? ReliableValues.Decode(parameters[i].ParameterType, packet.Args[position++]) : throw new RpcException(RpcError.InvalidPayload, "Too few reliable arguments");
                }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
                { throw new RpcException(RpcError.InvalidPayload, "Invalid argument " + i + ": " + ex.Message); }
            }
            if (packet.BinaryArgs != null ? position != packet.BinaryArgs.Length : packet.Args == null || position != packet.Args.Length)
                throw new RpcException(RpcError.InvalidPayload, "Argument count mismatch");
            lock (_gate)
                if (_disposed != 0 || _removedTargets.Contains(packet.Key) || !binding.Active ||
                    !_bindings.TryGetValue(packet.Key, out var latest) || !ReferenceEquals(latest, binding))
                    throw new RpcException(RpcError.Disposed, "Target unbound before dispatch");
            object? result;
            try { result = method.Invoke(binding.Instance, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
            }
            if (arrival == RpcDelivery.Unreliable || method.ReturnType == typeof(void) || method.ReturnType == typeof(Task)) return null;
            return ReliableValues.Encode(method.ReturnType.GenericTypeArguments[0], result);
        }).ConfigureAwait(false);
    }
    private void Receive(PeerId physicalSender, byte[] bytes)
        => ReceiveReliable(physicalSender, bytes);
    private void ReceiveReliable(PeerId physicalSender, ReadOnlyMemory<byte> memory)
    {
        var bytes = memory.Span;
        if (bytes.Length != 0 && bytes[0] == TypedRpcHeader.Magic)
        { ReceiveTyped(physicalSender, memory, RpcDelivery.Reliable); return; }
        if (Volatile.Read(ref _disposed) != 0 || IsClient && Volatile.Read(ref _hostDeparted) != 0) return;
        if (bytes.Length > ReliableCodec.MaxFrame) { ReportUnhandled(new RpcException(RpcError.LimitExceeded, "Reliable frame exceeds 1 MiB")); return; }
        Packet? packet;
        try { packet = ReliableCodec.Decode(bytes); }
        catch (RpcException ex) { ReportUnhandled(ex); return; }
        catch (Exception ex) { ReportUnhandled(new RpcException(RpcError.InvalidPayload, "Invalid reliable binary frame: " + ex.Message)); return; }
        if (packet == null || packet.V != 3 || packet.Scope != Scope || packet.Id == null || packet.Method == null ||
            packet.Service == null || packet.Entity == null || packet.Component == null || packet.Origin == null ||
            packet.Requester == null || packet.Destination == null || packet.Kind == null ||
            packet.Id.Length > 128 || packet.Method.Length > 1024 || packet.Service.Length > 256 ||
            packet.Entity.Length > 256 || packet.Component.Length > 256 || packet.Origin.Length > 128 ||
            packet.Requester.Length > 128 || packet.Destination.Length > 128 || packet.Kind.Length > 32 ||
            packet.Property?.Length > 256 || packet.Fault?.Length > 4096) return;
        if ((packet.Kind is "call" or "state" or "stateDelta" or "snapshotRequest") && string.IsNullOrWhiteSpace(packet.Service)) return;
        if (packet.Kind == "call" && !Enum.IsDefined(typeof(SendTo), packet.To)) return;
        if (IsClient && !physicalSender.Equals(HostPeerId)) return;
        if (IsHost && !_members.ContainsKey(physicalSender)) return;
        try { ReceiveValidated(physicalSender, packet); }
        catch (Exception ex)
        {
            var fault = ex as RpcException ?? new RpcException(RpcError.InvalidPayload, ex.Message);
            if (packet.Kind == "call" && IsHost && !packet.OneWay) SendFault(physicalSender, packet, fault);
            else ReportUnhandled(fault);
        }
    }
    private void ReceiveMemory(PeerId physicalSender, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0) return;
        if (bytes.Span[0] == TypedRpcHeader.Magic) ReceiveTyped(physicalSender, bytes, RpcDelivery.Reliable);
        else ReceiveReliable(physicalSender, bytes); // decode borrowed memory synchronously; only retained blobs are copied
    }
    internal TypedRpcWriter BeginTyped(object instance, TargetKey key, object token, ulong method, ulong fingerprint, SendTo route, RpcDelivery delivery, RpcTarget target)
    {
        TypedTarget numeric;
        lock (_gate)
        {
            Check();
            if (!IsReady) throw new RpcException(RpcError.Unauthorized, "Client not ready");
            if (_removedTargets.Contains(key) || !_bindings.TryGetValue(key, out var bound) || !bound.Active || bound.Initializing ||
                !ReferenceEquals(bound.Instance, instance) || !ReferenceEquals(bound.Token, token))
                throw new RpcException(RpcError.Disposed, "Typed target not bound");
            if (!bound.Typed.TryGetValue(method, out var definition) || definition.Fingerprint != fingerprint || definition.Route != route || definition.Delivery != delivery)
                throw new RpcException(RpcError.InvalidPayload, "Typed method fingerprint or route mismatch");
            numeric = bound.Numeric;
        }
        if (route == SendTo.All && !IsHost) throw new RpcException(RpcError.InvalidRole, "Only Host broadcasts");
        if (delivery == RpcDelivery.Unreliable && (_datagrams == null || !_datagrams.UnreliableEnabled))
            throw new RpcException(RpcError.Disconnected, "Unreliable lane unavailable or disabled");
        var destination = route == SendTo.Target ? target.Peer : HostPeerId;
        if (route == SendTo.Target && string.IsNullOrEmpty(destination.Value)) throw new RpcException(RpcError.InvalidPayload, "Target required");
        if (delivery == RpcDelivery.Unreliable)
        {
            if (route == SendTo.All)
            {
                var snapshot = _broadcastSnapshot;
                for (int i = 0; i < snapshot.Length; i++)
                    if (!_datagrams!.IsUnreliableReady(snapshot[i].Peer))
                        throw new RpcException(RpcError.Disconnected, "Unreliable broadcast peer not bound: " + snapshot[i].Peer);
            }
            else if (!(IsHost && route == SendTo.Host) && !(route == SendTo.Target && destination.Equals(LocalPeerId)) &&
                     !_datagrams!.IsUnreliableReady(IsClient ? HostPeerId : destination))
                throw new RpcException(RpcError.Disconnected, "Unreliable lane not bound");
        }
        var header = new TypedRpcHeader(delivery == RpcDelivery.Unreliable ? (byte)3 : (byte)1, route, _numericScope.A, _numericScope.B,
            numeric.A, numeric.B, TypedTarget.Peer(LocalPeerId), TypedTarget.Peer(destination),
            method, fingerprint, Guid.Empty, 0);
        return new TypedRpcWriter(this, key, destination, header,
            delivery == RpcDelivery.Unreliable ? _datagrams!.MaxUnreliablePayloadBytes : ReliableCodec.MaxFrame);
    }
    internal RpcCallValueContext EnterTypedLocal(TargetKey key, ulong method, ulong fingerprint, SendTo route)
    {
        if (!_bindings.TryGetValue(key, out var bound) || !bound.Active || bound.Initializing ||
            !bound.Typed.TryGetValue(method, out var definition) || definition.Fingerprint != fingerprint || definition.Route != route)
            throw new RpcException(RpcError.Disposed, "Typed local binding changed");
        if (IsHost && bound.Authorize != null &&
            (!_members.TryGetValue(LocalPeerId, out var member) || !member.Ready ||
             !bound.Authorize(new AuthorizationRequest(member, LocalPeerId, key, definition.Name))))
            throw new RpcException(RpcError.Unauthorized, "Denied local typed call");
        if (IsClient && bound.Authorize != null &&
            (!_members.TryGetValue(LocalPeerId, out var localMember) || !localMember.Ready ||
             !bound.Authorize(new AuthorizationRequest(localMember, LocalPeerId, key, definition.Name))))
            throw new RpcException(RpcError.Unauthorized, "Denied local typed call");
        lock (_gate)
            if (_disposed != 0 || _removedTargets.Contains(key) || !bound.Active ||
                !_bindings.TryGetValue(key, out var current) || !ReferenceEquals(current, bound))
                throw new RpcException(RpcError.Disposed, "Typed target removed during authorization");
        return RpcCallContext.EnterValue(LocalPeerId, LocalPeerId, key);
    }
    internal bool NeedsTypedRemote(SendTo route, PeerId destination)
        => route == SendTo.All ? IsHost && _broadcastSnapshot.Length != 0 :
            !(route == SendTo.Host && IsHost) && !(route == SendTo.Target && destination.Equals(LocalPeerId));
    internal void ReleaseTypedSlot() => Interlocked.Decrement(ref _typedInFlight);
    internal void ReserveTypedSlot() => TakeTypedSlot();
    private void TakeTypedSlot()
    {
        int prior;
        do
        {
            prior = Volatile.Read(ref _typedInFlight);
            if (prior == MaxTypedInFlight) throw new RpcException(RpcError.LimitExceeded, "Typed send backlog full");
        } while (Interlocked.CompareExchange(ref _typedInFlight, prior + 1, prior) != prior);
    }
    private async Task ObserveTypedSend(ValueTask operation, byte[] buffer)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception ex) { ReportUnhandled(ex); }
        finally { TypedPacketBuffer.Return(buffer); Interlocked.Decrement(ref _typedInFlight); }
    }
    private void SendTypedOne(PeerId peer, byte[] buffer, int count)
    {
        try
        {
            var operation = GetTypedOperation(peer, buffer, count);
            if (operation.IsCompletedSuccessfully)
            {
                operation.GetAwaiter().GetResult(); // ValueTasks are consumed once, even synchronously.
                TypedPacketBuffer.Return(buffer);
                Interlocked.Decrement(ref _typedInFlight);
            }
            else _ = ObserveTypedSend(operation, buffer);
        }
        catch
        {
            TypedPacketBuffer.Return(buffer);
            Interlocked.Decrement(ref _typedInFlight);
            throw;
        }
    }
    private ValueTask GetTypedOperation(PeerId peer, byte[] buffer, int count)
    {
        if (buffer[2] == 3)
        {
            if (_datagrams == null || !_datagrams.UnreliableEnabled || count > _datagrams.MaxUnreliablePayloadBytes || !_datagrams.IsUnreliableReady(peer))
                throw new RpcException(RpcError.Disconnected, "Unreliable recipient not bound or payload over limit");
            return new ValueTask(_datagrams.SendUnreliableAsync(peer, new ReadOnlyMemory<byte>(buffer, 0, count)));
        }
        return _memory != null
            ? _memory.SendMemoryAsync(peer, new ReadOnlyMemory<byte>(buffer, 0, count))
            : new ValueTask(_wire!.SendAsync(peer, buffer.AsSpan(0, count).ToArray()));
    }
    private bool CanBroadcastPeer(RoomMember member)
    {
        lock (_gate)
            return _disposed == 0 && _members.TryGetValue(member.Peer, out var current) && ReferenceEquals(member, current) &&
                member.Ready && !_heldBroadcastPeers.Contains(member.Peer);
    }
    private async Task SendTypedAwait(PeerId peer, byte[] buffer, int count)
    {
        try
        {
            if (_memory != null) await _memory.SendMemoryAsync(peer, new ReadOnlyMemory<byte>(buffer, 0, count)).ConfigureAwait(false);
            else await _wire!.SendAsync(peer, buffer.AsSpan(0, count).ToArray()).ConfigureAwait(false);
        }
        finally { TypedPacketBuffer.Return(buffer); Interlocked.Decrement(ref _typedInFlight); }
    }
    internal void SendTypedVoid(TargetKey key, PeerId destination, byte[] buffer, int count, bool localExecuted, bool slotReserved)
    {
        bool owned = true;
        try
        {
            Check();
            if (!TypedRpcHeader.TryRead(buffer.AsSpan(0, count), out var header) || header.Kind is not (1 or 3))
                throw new RpcException(RpcError.InvalidPayload, "Invalid typed notification");
            if (header.Route == SendTo.All)
            {
                if (!IsHost) throw new RpcException(RpcError.InvalidRole, "Only Host broadcasts");
                if (!localExecuted) ReceiveTypedLocal(key, LocalPeerId, header, buffer.AsMemory(TypedRpcHeader.Size, header.PayloadLength));
                var peers = _broadcastSnapshot;
                Span<ulong> selection = stackalloc ulong[4];
                selection.Clear();
                int eligible = 0;
                PeerId single = default;
                RoomMember? singleMember = null;
                for (int i = 0; i < peers.Length; i++)
                    if (CanBroadcastPeer(peers[i]))
                    { selection[i >> 6] |= 1UL << (i & 63); eligible++; single = peers[i].Peer; singleMember = peers[i]; }
                if (eligible == 0) return;
                if (eligible == 1)
                {
                    if (!CanBroadcastPeer(singleMember!)) return;
                    if (!slotReserved) TakeTypedSlot(); slotReserved = true; owned = false;
                    SendTypedOne(single, buffer, count);
                    return;
                }
                if (!slotReserved) TakeTypedSlot(); slotReserved = true; owned = false;
                var fanout = TypedFanoutLease.Rent(this, buffer, count, eligible);
                for (int i = 0; i < peers.Length; i++)
                    if ((selection[i >> 6] & (1UL << (i & 63))) != 0)
                    {
                        if (CanBroadcastPeer(peers[i])) fanout.Send(peers[i].Peer);
                        else fanout.CompleteOne();
                    }
                return;
            }
            if (header.Route == SendTo.Host && IsHost || header.Route == SendTo.Target && destination.Equals(LocalPeerId))
            { if (!localExecuted) ReceiveTypedLocal(key, LocalPeerId, header, buffer.AsMemory(TypedRpcHeader.Size, header.PayloadLength)); return; }
            var next = IsClient ? HostPeerId : destination;
            if (IsHost && (!_members.TryGetValue(next, out var member) || !member.Ready))
                throw new RpcException(RpcError.MissingTarget, "Typed recipient not ready");
            if (!slotReserved) TakeTypedSlot(); slotReserved = true; owned = false;
            SendTypedOne(next, buffer, count);
        }
        finally { if (owned) { TypedPacketBuffer.Return(buffer); if (slotReserved) ReleaseTypedSlot(); } }
    }
    internal Task SendTypedTask(TargetKey key, PeerId destination, byte[] bytes, int count)
        => SendTypedTaskCore<object>(key, destination, bytes, count, null);
    internal Task<T> SendTypedTask<T>(TargetKey key, PeerId destination, byte[] bytes, int count)
        => SendTypedTaskCore<T>(key, destination, bytes, count, typeof(T));
    private async Task<T> SendTypedTaskCore<T>(TargetKey key, PeerId destination, byte[] bytes, int count, Type? resultType)
    {
        if (!TypedRpcHeader.TryRead(bytes.AsSpan(0, count), out var header) || header.Kind != 1)
        { TypedPacketBuffer.Return(bytes); throw new RpcException(RpcError.InvalidPayload, "Invalid typed request"); }
        bytes[2] = 2; // request kind; fingerprint and method remain unchanged.
        if (destination.Equals(LocalPeerId) && (IsHost || header.Route == SendTo.Target))
        {
            try
            {
                var localHeader = new TypedRpcHeader(2, header.Route, header.ScopeA, header.ScopeB,
                    header.TargetA, header.TargetB, header.Sender, header.Destination, header.Method, header.Fingerprint,
                    header.Request, header.PayloadLength);
                var local = RunTypedLocal(key, LocalPeerId, localHeader,
                    new ReadOnlyMemory<byte>(bytes, TypedRpcHeader.Size, header.PayloadLength));
                var value = await local.ConfigureAwait(false);
                return resultType == null ? default! : (T)ReliableValues.Decode(resultType, value)!;
            }
            finally { TypedPacketBuffer.Return(bytes); }
        }
        var packet = new Packet { Kind = "call", Id = header.Request.ToString("N"),
            Service = key.Service, Entity = key.Entity, Component = key.Component, Destination = destination.Value,
            To = header.Route, ExpectedType = resultType };
        // The existing waiter supplies cancellation/timeout/reply semantics for Tasks.
        var next = IsClient ? HostPeerId : destination;
        bool started = false;
        try
        {
            var returned = await WaitFor(packet, next, async () =>
            {
                TakeTypedSlot();
                started = true;
                await SendTypedAwait(next, bytes, count).ConfigureAwait(false);
                return null;
            }, false, default).ConfigureAwait(false);
            return resultType == null ? default! : (T)ReliableValues.Decode(resultType, returned)!;
        }
        finally
        {
            // Once started, SendTypedAwait owns the rental through actual wire completion.
            if (!started) TypedPacketBuffer.Return(bytes);
        }
    }
    private void ReceiveTyped(PeerId physicalSender, ReadOnlyMemory<byte> borrowed, RpcDelivery arrival)
    {
        if (_disposed != 0 || borrowed.Length > ReliableCodec.MaxFrame ||
            !TypedRpcHeader.TryRead(borrowed.Span, out var header)) return;
        if ((header.Kind == 3 ? RpcDelivery.Unreliable : RpcDelivery.Reliable) != arrival ||
            arrival == RpcDelivery.Unreliable && (_datagrams == null || !_datagrams.UnreliableEnabled || borrowed.Length > _datagrams.MaxUnreliablePayloadBytes))
        { ReportUnhandled(new RpcException(RpcError.InvalidPayload, "Typed RPC arrival lane mismatch or oversized datagram")); return; }
        if (header.ScopeA != _numericScope.A || header.ScopeB != _numericScope.B) return;
        try
        {
            TargetKey key;
            PeerId sender;
            lock (_gate)
            {
                if (!_numericTargets.TryGetValue(new TypedTarget(header.TargetA, header.TargetB), out key) || _removedTargets.Contains(key))
                    throw new RpcException(RpcError.MissingTarget, "Unknown typed target");
                if (IsClient && !physicalSender.Equals(HostPeerId) || IsHost && !_members.ContainsKey(physicalSender))
                    throw new RpcException(RpcError.Unauthorized, "Unknown typed physical sender");
                sender = IsHost ? physicalSender : _numericPeers.TryGetValue(header.Sender, out var source)
                    ? source : throw new RpcException(RpcError.Unauthorized, "Unknown typed origin");
                if (IsHost && header.Route == SendTo.All) throw new RpcException(RpcError.InvalidRole, "Only Host broadcasts");
                if (IsHost && header.Route == SendTo.Target && header.Destination != TypedTarget.Peer(LocalPeerId))
                {
                    if (!_numericPeers.TryGetValue(header.Destination, out var recipient) ||
                        !_members.TryGetValue(recipient, out var m) || !m.Ready ||
                        !_members.TryGetValue(sender, out var s) || !s.Ready)
                        throw new RpcException(RpcError.Unauthorized, "Typed relay peer not ready");
                    var methodName = _bindings.TryGetValue(key, out var bound) && bound.Typed.TryGetValue(header.Method, out var known)
                        ? known.Name : "#" + header.Method.ToString("x16");
                    var request = new AuthorizationRequest(s, recipient, key, methodName);
                    if (!(_relayPolicies.TryGetValue(key, out var policy) && policy(request) ||
                        bound != null && (!bound.Owner.HasValue || bound.Owner.Value.Equals(sender)) &&
                        bound.Authorize != null && bound.Authorize(request)))
                        throw new RpcException(RpcError.Unauthorized, "Host typed relay policy required");
                    var forwarded = TypedPacketBuffer.RentRelay(borrowed.Length);
                    bool transferred = false;
                    try
                    {
                        borrowed.Span.CopyTo(forwarded);
                        BinaryPrimitives.WriteUInt64LittleEndian(forwarded.AsSpan(36, 8), TypedTarget.Peer(sender));
                        if (header.Kind == 2)
                        {
                            var id = header.Request.ToString("N");
                            if (_relays.Count >= MaxRelays || !_relays.TryAdd(id, (sender, recipient, key)))
                                throw new RpcException(RpcError.LimitExceeded, "Typed relay backlog full");
                            _ = ExpireRelay(id, sender);
                        }
                        TakeTypedSlot(); transferred = true;
                        SendTypedOne(recipient, forwarded, borrowed.Length);
                        return;
                    }
                    finally { if (!transferred) TypedPacketBuffer.Return(forwarded); }
                }
            }
            ReceiveTypedLocal(key, sender, header, borrowed.Slice(TypedRpcHeader.Size, header.PayloadLength));
        }
        catch (Exception ex)
        {
            if (header.Kind == 2 && IsHost)
            {
                _relays.TryRemove(header.Request.ToString("N"), out _);
                var error = ex as RpcException ?? new RpcException(RpcError.RemoteFault, ex.Message);
                SendFault(physicalSender, new Packet { Kind = "call", Id = header.Request.ToString("N"),
                    Service = _numericTargets.TryGetValue(new TypedTarget(header.TargetA, header.TargetB), out var target) ? target.Service : "",
                    To = header.Route }, error);
            }
            else if (header.Kind == 2 && IsClient)
            {
                var error = ex as RpcException ?? new RpcException(RpcError.RemoteFault, ex.Message);
                var origin = _numericPeers.TryGetValue(header.Sender, out var peer) && !peer.Equals(HostPeerId) ? peer.Value : "";
                SendBackground(HostPeerId, new Packet { Kind = "reply", Id = header.Request.ToString("N"),
                    Origin = LocalPeerId.Value, Requester = origin, To = header.Route,
                    Error = error.Error, Fault = error.Message });
            }
            else ReportUnhandled(ex);
        }
    }
    private void ReceiveTypedLocal(TargetKey key, PeerId sender, TypedRpcHeader header, ReadOnlyMemory<byte> payload)
    {
        var operation = RunTypedLocal(key, sender, header, payload);
        if (header.Kind != 2)
        {
            if (!operation.IsCompletedSuccessfully) _ = Observe(operation);
            return;
        }
        var replyTo = IsHost ? sender : HostPeerId;
        _ = FinishTypedRequest(operation, replyTo, header, key);
    }
    private Task<byte[]?> RunTypedLocal(TargetKey key, PeerId sender, TypedRpcHeader header, ReadOnlyMemory<byte> payload)
    {
        Bound binding;
        TypedMethod declaration;
        PeerId destination;
        lock (_gate)
        {
            if (_disposed != 0 || _removedTargets.Contains(key) || !_bindings.TryGetValue(key, out binding!) || !binding.Active || binding.Initializing)
                throw new RpcException(RpcError.Disposed, "Typed target removed before dispatch");
            if (!binding.Typed.TryGetValue(header.Method, out declaration!) || declaration.Fingerprint != header.Fingerprint ||
                declaration.Route != header.Route || declaration.Delivery != (header.Kind == 3 ? RpcDelivery.Unreliable : RpcDelivery.Reliable))
                throw new RpcException(RpcError.InvalidPayload, "Typed schema fingerprint, route or delivery mismatch");
            if (!_numericPeers.TryGetValue(header.Destination, out destination))
                throw new RpcException(RpcError.MissingTarget, "Typed destination absent from directory");
        }
        if (header.Route == SendTo.Host && !IsHost || header.Route == SendTo.All && !(IsHost ? sender.Equals(LocalPeerId) : sender.Equals(HostPeerId)) ||
            header.Route == SendTo.Target && header.Destination != TypedTarget.Peer(LocalPeerId))
            throw new RpcException(RpcError.InvalidRole, "Wrong typed executor");
        if (IsClient && !IsReady) throw new RpcException(RpcError.Unauthorized, "Client not ready");
        if (IsHost && (!_members.TryGetValue(sender, out var member) || !member.Ready ||
            binding.Owner.HasValue && !sender.Equals(LocalPeerId) && !binding.Owner.Value.Equals(sender) ||
            binding.Authorize != null && !binding.Authorize(new AuthorizationRequest(member, LocalPeerId, key, declaration.Name))))
            throw new RpcException(RpcError.Unauthorized, "Denied typed call by Host");
        if (IsClient && binding.Authorize != null &&
            (!_members.TryGetValue(sender, out var activeSender) || !activeSender.Ready ||
             !binding.Authorize(new AuthorizationRequest(activeSender, LocalPeerId, key, declaration.Name))))
            throw new RpcException(RpcError.Unauthorized, "Denied typed call by receiver");
        lock (_gate)
            if (_disposed != 0 || _removedTargets.Contains(key) || !binding.Active ||
                !_bindings.TryGetValue(key, out var activeBinding) || !ReferenceEquals(activeBinding, binding))
                throw new RpcException(RpcError.Disposed, "Typed target removed during authorization");
        if (header.Kind == 2)
            return RunTypedTaskReceiver(binding, declaration, key, sender, destination, payload);
        var before = RpcCallContext.EnterValue(sender, LocalPeerId, key);
        try
        {
            var reader = new TypedRpcReader(payload.Span, new RpcTarget(destination));
            var task = declaration.Receiver(binding.Instance, ref reader);
            reader.Complete();
            return task;
        }
        finally { RpcCallContext.ExitValue(before); }
    }
    private Task<byte[]?> RunTypedTaskReceiver(Bound binding, TypedMethod declaration, TargetKey key,
        PeerId sender, PeerId destination, ReadOnlyMemory<byte> payload)
    {
        return RpcCallContext.Run(new RpcCallContext(sender, LocalPeerId, key), () =>
        {
            var reader = new TypedRpcReader(payload.Span, new RpcTarget(destination));
            var task = declaration.Receiver(binding.Instance, ref reader);
            reader.Complete();
            return task;
        });
    }
    private async Task FinishTypedRequest(Task<byte[]?> operation, PeerId replyTo, TypedRpcHeader header, TargetKey key)
    {
        var reply = new Packet { Kind = "reply", Id = header.Request.ToString("N"), Origin = LocalPeerId.Value,
            Requester = IsClient && header.Sender != TypedTarget.Peer(HostPeerId) && _numericPeers.TryGetValue(header.Sender, out var requester)
                ? requester.Value : "", Service = key.Service, Entity = key.Entity, Component = key.Component,
            To = header.Route };
        try { reply.Result = await operation.ConfigureAwait(false); }
        catch (RpcException ex) { reply.Error = ex.Error; reply.Fault = ex.Message; }
        catch (Exception ex) { reply.Error = RpcError.RemoteFault; reply.Fault = ex.Message; }
        SendBackground(replyTo, reply);
    }
    private RpcDelivery ResolveDelivery(TargetKey key, string id, object? instance)
    {
        if (_bindings.TryGetValue(key, out var bound) && bound.Active && bound.Methods.TryGetValue(id, out var declaration))
            return declaration.Delivery;
        // Proxy methods are configured by their own attribute; this path is only used for remote-only contracts.
        if (instance is MethodInfo method) return method.GetCustomAttribute<RpcAttribute>()?.Delivery ?? RpcDelivery.Reliable;
        throw new RpcException(RpcError.MissingMethod, "Unconfigured RPC method " + id);
    }
    private Packet UnreliablePacket(TargetKey key, string id, object?[] args, SendTo route)
    {
        var destination = route == SendTo.Target ? args.OfType<RpcTarget>().SingleOrDefault().Peer.Value :
            route == SendTo.All ? "" : HostPeerId.Value;
        if (route == SendTo.Target && string.IsNullOrWhiteSpace(destination)) throw new RpcException(RpcError.InvalidPayload, "Target parameter required");
        return new Packet { Kind = "call", Scope = Scope, Service = key.Service, Entity = key.Entity, Component = key.Component,
            Method = id, To = route, Destination = destination, Origin = LocalPeerId.Value };
    }
    /// <summary>Exact application payload bytes produced by the unreliable codec; permits over-budget candidates up to 64 KiB.</summary>
    public int MeasureUnreliableCall(TargetKey key, MethodInfo method, object?[] args)
    {
        if (method == null || args == null) throw new ArgumentNullException(method == null ? nameof(method) : nameof(args));
        if (Volatile.Read(ref _disposed) != 0) throw new RpcException(RpcError.Disposed, "Scope disposed");
        if (string.IsNullOrWhiteSpace(key.Service)) throw new RpcException(RpcError.InvalidPayload, "Target key required for measurement");
        var schema = GetMeasureSchema(method);
        if (args.Length != schema.Types.Length) throw new RpcException(RpcError.InvalidPayload, "Argument count mismatch");
        if (schema.TargetIndex >= 0 && (args[schema.TargetIndex] is not RpcTarget target || string.IsNullOrEmpty(target.Peer.Value)))
            throw new RpcException(RpcError.InvalidPayload, "Target peer required for measurement");
        if (schema.Typed is { })
        {
            // B6's header is fixed at 88 bytes; IDs, target strings and hashes have no
            // effect on length. Don't construct a Packet or hash a TargetKey per candidate.
            using var typedWriter = new TypedRpcWriter(this, key, default,
                new TypedRpcHeader(3, schema.Route, 0, 0, 0, 0, 0, 0, 0, 0, Guid.Empty, 0),
                MaxUnreliableCandidateBytes);
            for (int i = 0; i < schema.Types.Length; i++)
                if (i != schema.TargetIndex) typedWriter.WriteBoxed(schema.Types[i], args[i]);
            return typedWriter.LengthBytes;
        }
        using var encoded = EncodeUnreliable(UnreliablePacket(key, schema.OldId!, args, schema.Route), method, args);
        return encoded.LengthBytes;
    }
    private void SendUnreliableCall(TargetKey key, string id, object?[] args, SendTo route, MethodInfo? proxyMethod)
    {
        if (route == SendTo.All && !IsHost) throw new RpcException(RpcError.InvalidRole, "Only Host may send All");
        var lane = _datagrams ?? throw new RpcException(RpcError.Disconnected, "Unreliable lane unavailable");
        if (!lane.UnreliableEnabled) throw new RpcException(RpcError.Disconnected, "Unreliable lane disabled");
        var packet = UnreliablePacket(key, id, args, route);
        if (_bindings.TryGetValue(key, out var binding) && binding.Methods.TryGetValue(id, out var definition) && definition.Route != route)
            throw new RpcException(RpcError.InvalidPayload, "RPC route disagrees with declaration");
        var destination = route == SendTo.All ? HostPeerId : new PeerId(packet.Destination);
        PeerId[] recipients;
        lock (_gate)
        {
            if (route == SendTo.All) recipients = ReadyPeersLocked().Where(p => !_heldBroadcastPeers.Contains(p)).ToArray();
            else if (route == SendTo.Target && destination.Equals(LocalPeerId) || route == SendTo.Host && IsHost) recipients = Array.Empty<PeerId>();
            else
            {
                var next = IsClient ? HostPeerId : destination;
                if (IsHost && (!_members.TryGetValue(next, out var m) || !m.Ready)) throw new RpcException(RpcError.MissingTarget, "Peer not ready");
                recipients = new[] { next };
            }
        }
        foreach (var peer in recipients)
            if (!lane.IsUnreliableReady(peer)) throw new RpcException(RpcError.Disconnected, "Unreliable lane not bound for " + peer);
        // Complete serialization before returning to the caller. The rented buffer is held across
        // every asynchronous send, including faults. Never give a caller's mutable segment to the wire.
        var method = _bindings.TryGetValue(key, out var outgoing) && outgoing.Active && outgoing.Methods.TryGetValue(id, out var declaration)
            ? declaration.Body : proxyMethod ?? throw new RpcException(RpcError.MissingMethod, "Unreliable schema unavailable");
        var encoded = EncodeUnreliable(packet, method, args);
        bool handedOff = false;
        bool reserved = false;
        try
        {
            if (encoded.LengthBytes > lane.MaxUnreliablePayloadBytes) throw new RpcException(RpcError.LimitExceeded, "Unreliable payload exceeds lane maximum");
            if (recipients.Length != 0) { ReserveUnreliableSend(); reserved = true; }
            if (route == SendTo.All || recipients.Length == 0)
            {
                packet.BinaryArgs = args.Where(a => a is not RpcTarget).ToArray();
                var local = Execute(packet, LocalPeerId, RpcDelivery.Unreliable);
                if (local.IsCompleted) local.GetAwaiter().GetResult();
                else _ = Observe(local);
            }
            if (reserved) { _ = SendUnreliableToMany(recipients, encoded); handedOff = true; }
        }
        finally
        {
            if (!handedOff)
            {
                if (reserved) Interlocked.Decrement(ref _unreliableInFlight);
                encoded.Dispose();
            }
        }
    }
    private void StartUnreliableSend(PeerId[] peers, PooledPacket encoded)
    {
        ReserveUnreliableSend();
        _ = SendUnreliableToMany(peers, encoded);
    }
    private void ReserveUnreliableSend()
    {
        int prior;
        do
        {
            prior = Volatile.Read(ref _unreliableInFlight);
            if (prior >= MaxUnreliableInFlight) throw new RpcException(RpcError.LimitExceeded, "Unreliable send backlog full");
        } while (Interlocked.CompareExchange(ref _unreliableInFlight, prior + 1, prior) != prior);
    }
    private async Task SendUnreliableToMany(PeerId[] peers, PooledPacket encoded)
    {
        var lane = _datagrams!;
        try
        {
            var tasks = new Task[peers.Length];
            for (int i = 0; i < peers.Length; i++)
            {
                try { tasks[i] = lane.SendUnreliableAsync(peers[i], encoded.Memory); }
                catch (Exception ex) { tasks[i] = Task.FromException(ex); }
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) { ReportUnhandled(ex); }
        finally { encoded.Dispose(); Interlocked.Decrement(ref _unreliableInFlight); }
    }
    private void ReceiveUnreliable(PeerId physicalSender, ReadOnlyMemory<byte> borrowed)
    {
        if (_disposed != 0 || _datagrams == null || !_datagrams.UnreliableEnabled || borrowed.Length > _datagrams.MaxUnreliablePayloadBytes) return;
        if (borrowed.Length != 0 && borrowed.Span[0] == TypedRpcHeader.Magic)
        { ReceiveTyped(physicalSender, borrowed, RpcDelivery.Unreliable); return; }
        try
        {
            // Decode entirely before callback return: the transport owns and may reuse borrowed memory.
            var packet = DecodeUnreliable(borrowed.Span, out var argumentOffset);
            if (packet.Scope != Scope || packet.Service.Length is < 1 or > 256 || packet.Entity.Length > 256 || packet.Component.Length > 256 ||
                packet.Method.Length is < 1 or > 1024 || packet.Origin.Length is < 1 or > 128 || packet.Destination.Length > 128 ||
                !Enum.IsDefined(typeof(SendTo), packet.To)) throw new RpcException(RpcError.InvalidPayload, "Invalid unreliable envelope");
            lock (_gate)
                if (_disposed != 0 || _removedTargets.Contains(packet.Key)) throw new RpcException(RpcError.Disposed, "Target removed or runtime disposed");
            if (IsClient && !physicalSender.Equals(HostPeerId) || IsHost && !_members.ContainsKey(physicalSender))
                throw new RpcException(RpcError.Unauthorized, "Unrecognized datagram sender");
            if (IsHost)
            {
                if (packet.To == SendTo.All) throw new RpcException(RpcError.InvalidRole, "Only Host sends All");
                packet.Origin = physicalSender.Value;
                if (!_members.TryGetValue(physicalSender, out var sender) || !sender.Ready) throw new RpcException(RpcError.Unauthorized, "Sender not ready");
                if (packet.To == SendTo.Target && packet.Destination != LocalPeerId.Value)
                {
                    var target = new PeerId(packet.Destination);
                    if (!_members.TryGetValue(target, out var recipient) || !recipient.Ready) throw new RpcException(RpcError.MissingTarget, "Recipient not ready");
                    lock (_gate) if (_removedTargets.Contains(packet.Key)) throw new RpcException(RpcError.Disposed, "Target removed");
                    var request = new AuthorizationRequest(sender, target, packet.Key, packet.Method);
                    bool permitted = _relayPolicies.TryGetValue(packet.Key, out var policy) ? policy(request) :
                        _bindings.TryGetValue(packet.Key, out var bound) && bound.Active && bound.Methods.TryGetValue(packet.Method, out var decl) &&
                        decl.Route == SendTo.Target && decl.Delivery == RpcDelivery.Unreliable &&
                        (!bound.Owner.HasValue || bound.Owner.Value.Equals(physicalSender)) && bound.Authorize != null && bound.Authorize(request);
                    if (!permitted) throw new RpcException(RpcError.Unauthorized, "Host relay policy required");
                    if (!_datagrams.IsUnreliableReady(target) || !_datagrams.UnreliableEnabled) throw new RpcException(RpcError.Disconnected, "Recipient datagram lane unavailable");
                    // Relay owns a newly encoded routing header plus opaque, bounded MemoryPack
                    // argument bytes before returning the borrowed receive callback buffer.
                    var encoded = EncodeRelayed(packet, borrowed.Span.Slice(argumentOffset));
                    bool handedOff = false;
                    try
                    {
                        if (encoded.LengthBytes > _datagrams.MaxUnreliablePayloadBytes) throw new RpcException(RpcError.LimitExceeded, "Relayed datagram too large");
                        StartUnreliableSend(new[] { target }, encoded);
                        handedOff = true;
                    }
                    finally { if (!handedOff) encoded.Dispose(); }
                    return;
                }
            }
            else
            {
                if (packet.To == SendTo.Host || packet.To == SendTo.Target && packet.Destination != LocalPeerId.Value ||
                    packet.To == SendTo.All && packet.Origin != HostPeerId.Value)
                    throw new RpcException(RpcError.Unauthorized, "Invalid datagram origin or destination");
                if (packet.To == SendTo.Target &&
                    (!_members.TryGetValue(new PeerId(packet.Origin), out var source) || !source.Ready))
                    throw new RpcException(RpcError.Unauthorized, "Original sender is not ready");
            }
            if (!_bindings.TryGetValue(packet.Key, out var receiver) || !receiver.Active ||
                !receiver.Methods.TryGetValue(packet.Method, out var definition))
                throw new RpcException(RpcError.MissingMethod, "Unreliable declaration unavailable");
            if (definition.Route != packet.To || definition.Delivery != RpcDelivery.Unreliable)
                throw new RpcException(RpcError.InvalidPayload, "Unreliable arrival disagrees with declaration");
            packet.BinaryArgs = DecodeUnreliableArguments(definition.Body, borrowed.Span.Slice(argumentOffset));
            _ = Observe(Execute(packet, new PeerId(packet.Origin), RpcDelivery.Unreliable));
        }
        catch (Exception ex) { ReportUnhandled(ex); } // No ACK, reply, or reliable fallback.
    }
    private void ReceiveMembers(Packet packet)
    {
        lock (_gate) if (_disposed != 0 || packet.Version <= _memberVersion) return;
        if (packet.Version <= 0 || packet.Members == null || packet.Members.Count is < 1 or > MaxMembers ||
            packet.RemovedTargets == null || packet.RemovedTargets.Count > MaxTargets)
            throw new RpcException(RpcError.InvalidPayload, "Invalid member directory");
        var members = new List<RoomMember>(packet.Members.Count);
        var removed = new HashSet<TargetKey>();
        foreach (var record in packet.RemovedTargets)
        {
            if (record == null) throw new RpcException(RpcError.InvalidPayload, "Invalid removed target");
            try { removed.Add(new TargetKey(record.Service, record.Entity, record.Component)); }
            catch (ArgumentException ex) { throw new RpcException(RpcError.InvalidPayload, ex.Message); }
        }
        var peers = new HashSet<PeerId>();
        var numericPeers = new Dictionary<ulong, PeerId>();
        var players = new HashSet<string>(StringComparer.Ordinal);
        var steam = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in packet.Members)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Peer) || record.Peer.Length > 128) throw new RpcException(RpcError.InvalidPayload, "Invalid peer in directory");
            var member = new RoomMember(new PeerId(record.Peer), record.PlayerId, record.SteamId, record.Ready);
            ValidateMember(member);
            var numeric = TypedTarget.Peer(member.Peer);
            if (numericPeers.TryGetValue(numeric, out var priorPeer) && !priorPeer.Equals(member.Peer))
                throw new RpcException(RpcError.InvalidPayload, "Numeric peer identity collision");
            numericPeers[numeric] = member.Peer;
            if (!peers.Add(member.Peer) || member.PlayerId != null && !players.Add(member.PlayerId) || member.SteamId != null && !steam.Add(member.SteamId))
                throw new RpcException(RpcError.InvalidPayload, "Ambiguous member directory");
            members.Add(member);
        }
        if (!members.Any(m => m.Peer.Equals(HostPeerId) && m.Ready)) throw new RpcException(RpcError.InvalidPayload, "Missing ready Host");
        bool exhausted;
        lock (_gate) exhausted = _removedTargets.Union(removed).Count() > MaxTargets;
        if (exhausted)
        {
            Leave(HostPeerId, false); // Never remain ready with an unrepresentable authority tombstone set.
            throw new RpcException(RpcError.LimitExceeded, "Removed target history exhausted; use a fresh room scope");
        }
        // Tombstones are monotonically accumulated by the Host. Apply them before enabling
        // readiness, so a newly admitted peer cannot briefly execute a deleted local Target.
        foreach (var key in removed) RemoveTarget(key);
        Waiter[] revokedWaiters;
        TargetKey[] refresh;
        bool removedSelf;
        lock (_gate)
        {
            if (_disposed != 0 || packet.Version <= _memberVersion) return;
            var previouslyReady = _ready != 0;
            _members.Clear();
            foreach (var member in members) _members[member.Peer] = member;
            _memberVersion = packet.Version;
            RefreshMembersLocked();
            removedSelf = !_members.ContainsKey(LocalPeerId);
            UpdateClientReadinessLocked();
            revokedWaiters = !removedSelf && previouslyReady && _ready == 0 ? RemoveAllPendingLocked() : Array.Empty<Waiter>();
            refresh = !previouslyReady && _ready != 0 ? _bindings.Keys.ToArray() : Array.Empty<TargetKey>();
        }
        if (removedSelf) { Leave(HostPeerId, false); return; }
        foreach (var waiter in revokedWaiters) waiter.Completion.TrySetException(new RpcException(RpcError.Unauthorized, "Room member no longer ready"));
        if (refresh.Length != 0) RequestBoundSnapshots(refresh);
        NotifyMembers(packet.Version);
    }
    private void ReceiveValidated(PeerId physicalSender, Packet packet)
    {
        if (packet.Kind == "timeRequest" || packet.Kind == "timeReply") { ReceiveTime(physicalSender, packet); return; }
        if (packet.Kind == "membersRequest" && IsHost)
        {
            Packet? directory = null;
            lock (_gate)
                if (_members.ContainsKey(physicalSender)) directory = MemberPacketLocked();
            if (directory != null) SendBackground(physicalSender, directory);
            return;
        }
        if (packet.Kind == "members" && IsClient) { ReceiveMembers(packet); return; }
        if (packet.Kind == "memberRevoked" && IsClient)
        {
            if (packet.Destination == LocalPeerId.Value && packet.Version > MemberVersion) Leave(HostPeerId, false);
            return;
        }
        if (packet.Kind == "targetRemoved" && IsClient) { RemoveTarget(packet.Key); return; }
        if (packet.Kind == "reply")
        {
            if (IsHost && !physicalSender.Value.Equals(packet.Origin)) return;
            if (IsHost && packet.To == SendTo.All)
            {
                if (_fanout.TryRemove((packet.Id, physicalSender), out _) && packet.Fault != null)
                    ReportUnhandled(new RpcException(packet.Error, packet.Fault));
                return;
            }
            if (IsHost && !string.IsNullOrEmpty(packet.Requester) && packet.Requester != LocalPeerId.Value)
            {
                if (_relays.TryGetValue(packet.Id, out var relay) && relay.Requester.Value == packet.Requester && relay.Destination.Equals(physicalSender) &&
                    _relays.TryRemove(packet.Id, out _) && _members.ContainsKey(relay.Requester))
                    SendBackground(relay.Requester, packet);
                return;
            }
            if (_pending.TryGetValue(packet.Id, out var waiter) && waiter.Destination.Equals(physicalSender))
            {
                if (packet.Fault != null) waiter.Completion.TrySetException(new RpcException(packet.Error, packet.Fault));
                else
                {
                    try
                    {
                        if (waiter.ResultType != null) ReliableValues.Preflight(waiter.ResultType, packet.Result ?? throw new RpcException(RpcError.InvalidPayload, "Missing result"));
                        else if (packet.Result != null) throw new RpcException(RpcError.InvalidPayload, "Unexpected result");
                        waiter.Completion.TrySetResult(packet.Result);
                    }
                    catch (Exception ex) { waiter.Completion.TrySetException(ex); }
                }
            }
            return;
        }
        if (packet.Kind == "state" && IsClient) { ReceiveState(packet); return; }
        if (packet.Kind == "stateDelta" && IsClient) { ReceiveCollection(packet, true); return; }
        if (packet.Kind == "snapshotRequest" && IsHost)
        {
            if (!_members.TryGetValue(physicalSender, out var member) || !CanReceiveState(member, packet.Key)) return;
            foreach (var item in _state.Where(s => s.Key.Item1.Equals(packet.Key))) SendBackground(physicalSender, StatePacket(item.Key.Item1, item.Key.Item2, item.Value.Version, item.Value.Value));
            SendCollectionSnapshots(member, packet.Key);
            return;
        }
        if (packet.Kind != "call") return;
        if (_bindings.TryGetValue(packet.Key, out var receiving) && receiving.Active &&
            receiving.Methods.TryGetValue(packet.Method, out var receivingMethod) && receivingMethod.Delivery != RpcDelivery.Reliable)
            throw new RpcException(RpcError.InvalidPayload, "RPC arrival lane disagrees with declaration");
        // An unreliable declaration must never be laundered through the reliable lane, including Host relays.
        if (IsHost && packet.To == SendTo.Target && packet.Destination != LocalPeerId.Value &&
            _bindings.TryGetValue(packet.Key, out var routed) && routed.Methods.TryGetValue(packet.Method, out var declared) && declared.Delivery != RpcDelivery.Reliable)
            throw new RpcException(RpcError.InvalidPayload, "RPC arrival lane disagrees with declaration");
        if (IsHost)
        {
            if (packet.To == SendTo.All) { SendFault(physicalSender, packet, new RpcException(RpcError.InvalidRole, "Only Host can initiate All")); return; }
            packet.Origin = physicalSender.Value; // never trust claimed sender
            if (!_members.TryGetValue(physicalSender, out var requester) || !requester.Ready)
            { SendFault(physicalSender, packet, new RpcException(RpcError.Unauthorized, "Requester is not ready")); return; }
            if (packet.To == SendTo.Target && packet.Destination != LocalPeerId.Value)
            {
                if (!_members.TryGetValue(new PeerId(packet.Destination), out var member) || !member.Ready) { SendFault(physicalSender, packet, new RpcException(RpcError.MissingTarget, "Peer not ready")); return; }
                // Relays authenticate originating session and authorize against registered Host policy when available.
                var request = new AuthorizationRequest(requester, member.Peer, packet.Key, packet.Method);
                var permitted = _relayPolicies.TryGetValue(packet.Key, out var policy) ? policy(request) :
                    _bindings.TryGetValue(packet.Key, out var bound) && (!bound.Owner.HasValue || bound.Owner.Value.Equals(physicalSender)) && bound.Authorize != null && bound.Authorize(request);
                if (!permitted)
                { SendFault(physicalSender, packet, new RpcException(RpcError.Unauthorized, "Host relay policy required")); return; }
                packet.Requester = physicalSender.Value;
                if (packet.OneWay) { SendBackground(member.Peer, packet); return; }
                RpcException? relayError = null;
                lock (_gate)
                {
                    if (_disposed != 0 || !_members.TryGetValue(physicalSender, out var activeSender) || !activeSender.Ready ||
                        !_members.TryGetValue(member.Peer, out var activeRecipient) || !activeRecipient.Ready)
                        relayError = new RpcException(RpcError.Disconnected, "Relay peer left");
                    else if (_removedTargets.Contains(packet.Key) || !_relayPolicies.ContainsKey(packet.Key) &&
                        (!_bindings.TryGetValue(packet.Key, out var activeBinding) || !activeBinding.Active))
                        relayError = new RpcException(RpcError.Disposed, "Relay target removed");
                    else if (_relays.Count >= MaxRelays) relayError = new RpcException(RpcError.LimitExceeded, "Relay limit reached");
                    else if (!_relays.TryAdd(packet.Id, (physicalSender, member.Peer, packet.Key))) relayError = new RpcException(RpcError.InvalidPayload, "Duplicate request");
                }
                if (relayError != null) { SendFault(physicalSender, packet, relayError); return; }
                _ = ExpireRelay(packet.Id, physicalSender);
                SendBackground(member.Peer, packet); return;
            }
        }
        else if (packet.To == SendTo.Host || packet.Destination != LocalPeerId.Value || string.IsNullOrWhiteSpace(packet.Origin)) return;
        if (packet.OneWay) _ = HandleOneWay(packet, IsHost ? physicalSender : new PeerId(packet.Origin));
        else _ = Handle(packet, IsHost ? physicalSender : new PeerId(packet.Origin), IsHost ? physicalSender : HostPeerId);
    }
    private async Task HandleOneWay(Packet packet, PeerId sender)
    {
        try { await Execute(packet, sender).ConfigureAwait(false); }
        catch (Exception ex) { ReportUnhandled(ex); }
    }
    private async Task Handle(Packet packet, PeerId sender, PeerId replyTo)
    {
        var reply = packet.Copy(); reply.Kind = "reply"; reply.Origin = LocalPeerId.Value;
        try { reply.Result = await Execute(packet, sender).ConfigureAwait(false); }
        catch (RpcException ex) { reply.Fault = ex.Message; reply.Error = ex.Error; }
        catch (Exception ex) { reply.Fault = ex.Message; reply.Error = RpcError.RemoteFault; }
        SendBackground(replyTo, reply);
    }
    private void SendFault(PeerId peer, Packet packet, RpcException error)
    {
        if (packet.OneWay) { ReportUnhandled(error); return; }
        var reply = packet.Copy(); reply.Kind = "reply"; reply.Origin = LocalPeerId.Value; reply.Error = error.Error; reply.Fault = error.Message; SendBackground(peer, reply);
    }
    private async Task ExpireRelay(string id, PeerId requester)
    {
        try { await Task.Delay(Timeout, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (_relays.TryRemove(id, out var route) && _members.ContainsKey(requester))
            SendBackground(requester, new Packet { Kind = "reply", Id = id, Origin = LocalPeerId.Value, Error = RpcError.Timeout, Fault = "Relay result unknown" });
    }
    private async Task ExpireFanout(string id, PeerId peer)
    {
        try { await Task.Delay(Timeout, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (_fanout.TryRemove((id, peer), out _) && _disposed == 0)
            ReportUnhandled(new RpcException(RpcError.Timeout, "All delivery result unknown for " + peer));
    }
    private Task Send(PeerId peer, Packet packet)
    {
        Check(); packet.Scope = Scope; packet.Origin = string.IsNullOrEmpty(packet.Origin) ? LocalPeerId.Value : packet.Origin;
        var bytes = ReliableCodec.Encode(packet);
        return _wire!.SendAsync(peer, bytes);
    }
    private void SendBackground(PeerId peer, Packet packet) => _ = ObserveSend(peer, packet);
    private async Task ObserveSend(PeerId peer, Packet packet)
    {
        try { await Send(peer, packet).ConfigureAwait(false); }
        catch (RpcException ex) when (ex.Error == RpcError.Disconnected && IsHost && IsRetiredPeer(peer))
        {
            // A queued directory/state/All write may run after authenticated leave has
            // retired this peer. Its fanout/pending calls were cancelled by Leave; there
            // is no active delivery to report. Active-peer failures are still surfaced.
        }
        catch (Exception ex) { ReportUnhandled(ex); }
    }
    private bool IsRetiredPeer(PeerId peer) { lock (_gate) return _retiredPeers.Contains(peer); }
    private Packet StatePacket(TargetKey key, string property, long version, byte[] value) => new() { Kind = "state", Service = key.Service, Entity = key.Entity, Component = key.Component, Property = property, Version = version, Value = value };
    private bool CanReceiveState(RoomMember member, TargetKey key)
    {
        if (!IsHost || !member.Ready || member.Peer.Equals(LocalPeerId) ||
            !_members.TryGetValue(member.Peer, out var current) || !ReferenceEquals(current, member) ||
            !_bindings.TryGetValue(key, out var bound) || !bound.Active || bound.Initializing || bound.StateFingerprints.Count == 0 ||
            bound.Owner.HasValue && !bound.Owner.Value.Equals(member.Peer)) return false;
        if (bound.Authorize != null)
        {
            bool permitted;
            try { permitted = bound.Authorize(new AuthorizationRequest(member, LocalPeerId, key, StateAuthorizationMethod)); }
            catch (Exception ex) { ReportUnhandled(ex); return false; } // fail closed; directory admission remains atomic
            if (!permitted) return false;
        }
        return bound.Active && _members.TryGetValue(member.Peer, out current) && ReferenceEquals(current, member) &&
            _bindings.TryGetValue(key, out var latest) && ReferenceEquals(latest, bound);
    }
    internal void SetState(object instance, TargetKey key, object token, string property, object? value)
    {
        if (!_bindings.TryGetValue(key, out var schemaBinding) || !schemaBinding.Active || schemaBinding.Initializing || !ReferenceEquals(schemaBinding.Instance, instance) || !ReferenceEquals(schemaBinding.Token, token))
            throw new RpcException(RpcError.Disposed, "Unbound synchronized property");
        if (!schemaBinding.Properties.TryGetValue(property, out var schema))
            throw new RpcException(RpcError.MissingMethod, "Unknown synchronized property");
        var serialized = EncodeState(schemaBinding, property, value);
        long version;
        object? oldValue;
        RoomMember[] recipients;
        lock (_gate)
        {
            Check(); RequireHost();
            if (!_bindings.TryGetValue(key, out var bound) || !bound.Active || bound.Initializing || !ReferenceEquals(bound.Instance, instance) || !ReferenceEquals(bound.Token, token)) throw new RpcException(RpcError.Disposed, "Unbound state");
            if (!bound.Properties.TryGetValue(property, out var entry)) throw new RpcException(RpcError.MissingMethod, "Unknown state property");
            lock (bound)
            {
                if (_state.TryGetValue((key, property), out var previous) && previous.Value.AsSpan().SequenceEqual(serialized)) return;
                if (bound.Versions.TryGetValue(property, out var exhausted) && exhausted == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "SyncVar revision exhausted");
                oldValue = entry.Property.GetValue(instance);
                entry.Raw.Invoke(instance, new[] { value });
                version = bound.Versions.TryGetValue(property, out var v) ? v + 1 : 1;
                bound.Versions[property] = version;
                _state[(key, property)] = (version, serialized);
            }
            recipients = _members.Values.Where(m => m.Ready && !m.Peer.Equals(LocalPeerId)).ToArray();
        }
        foreach (var member in recipients.Where(m => CanReceiveState(m, key))) SendBackground(member.Peer, StatePacket(key, property, version, serialized));
        InvokeStateHook(schemaBinding, property, oldValue, value);
        NotifyState(key, property);
    }
    private void ReceiveState(Packet packet)
    {
        if (packet.Property == null || packet.Version < 0) return;
        if (packet.Value == null || packet.Value.Length > MaxStateValueBytes)
            throw new RpcException(RpcError.LimitExceeded, "Invalid synchronized value size");
        if (_bindings.TryGetValue(packet.Key, out var collectionOwner) && collectionOwner.Collections.ContainsKey(packet.Property))
        { ReceiveCollection(packet, false); return; }
        var identity = (packet.Key, packet.Property);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Bound? bound;
            lock (_gate)
            {
                if (_disposed != 0 || _removedTargets.Contains(packet.Key)) return;
                if (_state.TryGetValue(identity, out var prior) && prior.Version >= packet.Version) return;
                _bindings.TryGetValue(packet.Key, out bound);
            }
            // Validation is outside the gate: DTO conversion may execute user callbacks.
            if (bound != null && bound.Active && bound.Properties.TryGetValue(packet.Property, out var definition))
                DecodeState(bound, packet.Property, packet.Value);
            else if (bound != null && bound.Active && !bound.StateFingerprints.ContainsKey(packet.Property))
                throw new RpcException(RpcError.InvalidPayload, "Unknown SyncVar member; incompatible state contract");
            lock (_gate)
            {
                if (_disposed != 0 || _removedTargets.Contains(packet.Key)) return;
                if (_state.TryGetValue(identity, out var prior) && prior.Version >= packet.Version) return;
                _bindings.TryGetValue(packet.Key, out var current);
                if (!ReferenceEquals(bound, current)) continue;
                if (!_state.ContainsKey(identity) && StateEntryCountLocked() >= MaxStateEntries) throw new RpcException(RpcError.LimitExceeded, "Synchronized state limit reached");
                _state[identity] = (packet.Version, packet.Value);
            }
            if (bound != null) Apply(bound, packet.Property, packet.Version, packet.Value);
            return;
        }
        throw new RpcException(RpcError.Disconnected, "State binding changed repeatedly during receive");
    }
    private void Apply(Bound bound, string property, long version, byte[] value)
    {
        if (!bound.Properties.TryGetValue(property, out var entry)) return;
        var converted = DecodeState(bound, property, value);
        bool changed;
        object? oldValue;
        lock (_gate)
        {
            if (_disposed != 0 || !bound.Active || !_bindings.TryGetValue(bound.Key, out var current) || !ReferenceEquals(bound, current) ||
                _state.TryGetValue((bound.Key, property), out var latest) && latest.Version > version) return;
            lock (bound)
            {
                if (bound.Versions.TryGetValue(property, out var prior) && prior >= version) return;
                oldValue = entry.Property.GetValue(bound.Instance);
                changed = !EncodeState(bound, property, oldValue).AsSpan().SequenceEqual(value);
                if (changed) entry.Raw.Invoke(bound.Instance, new[] { converted });
                bound.Versions[property] = version;
            }
        }
        if (changed) { InvokeStateHook(bound, property, oldValue, converted); NotifyState(bound.Key, property); }
        NotifySynchronized(bound);
    }
    private void Left(PeerId peer) => Leave(peer, false);
    private void Leave(PeerId peer, bool sendRevocation)
    {
        Packet? directory = null;
        Packet? revocation = null;
        PeerId[] recipients = Array.Empty<PeerId>();
        var faults = new List<(PeerId Peer, string Id)>();
        var waiters = new List<Waiter>();
        bool clientDeparted = false;
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (IsHost) _heldBroadcastPeers.Remove(peer);
            if (IsHost && _members.TryRemove(peer, out _))
            {
                _clockReplies.Remove(peer);
                if (_retiredPeers.Count < MaxRetiredPeers) _retiredPeers.Add(peer);
                else _retiredPeerLimitReached = true;
                if (_memberVersion == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Member directory version exhausted");
                _memberVersion++;
                RefreshMembersLocked();
                directory = MemberPacketLocked();
                recipients = ReadyPeersLocked();
                if (sendRevocation) revocation = new Packet { Kind = "memberRevoked", Version = _memberVersion, Destination = peer.Value };
            }
            if (IsClient && peer.Equals(HostPeerId))
            {
                StopNetworkTime();
                _ready = 0;
                _confirmed = 0;
                _hostDeparted = 1; // A fresh session/room epoch needs a fresh runtime, never recycle identity.
                _members.Clear(); _state.Clear(); RefreshMembersLocked(); _memberVersion = 0;
                foreach (var b in _bindings.Values) DetachCollections(b, false);
                _syncRequests.Clear();
                directory = null;
                clientDeparted = true;
            }
            foreach (var relay in _relays.ToArray()) if (relay.Value.Requester.Equals(peer) || relay.Value.Destination.Equals(peer))
                if (_relays.TryRemove(relay.Key, out var route) && route.Destination.Equals(peer) && !route.Requester.Equals(peer)) faults.Add((route.Requester, relay.Key));
            foreach (var fanout in _fanout.Keys.Where(k => k.Peer.Equals(peer)).ToArray()) _fanout.TryRemove(fanout, out _);
            foreach (var entry in _pending.ToArray())
                if ((clientDeparted || entry.Value.Destination.Equals(peer)) && _pending.TryRemove(entry.Key, out var waiter)) waiters.Add(waiter);
        }
        foreach (var waiter in waiters) waiter.Completion.TrySetException(new RpcException(RpcError.Disconnected, "Peer left"));
        if (clientDeparted) NotifyMembers(0);
        if (revocation != null) SendBackground(peer, revocation);
        if (directory != null) PublishMembers(directory, recipients);
        foreach (var fault in faults) SendBackground(fault.Peer, new Packet { Kind = "reply", Id = fault.Id, Origin = LocalPeerId.Value, Error = RpcError.Disconnected, Fault = "Target peer left" });
    }
    public void Dispose()
    {
        Waiter[] waiters;
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            StopNetworkTime(); _clockReplies.Clear();
            foreach (var bound in _bindings.Values) { bound.Active = false; DetachCollections(bound, false); DispatchBridge.Detach(bound.Instance, this); }
            _syncRequests.Clear();
            _bindings.Clear(); _state.Clear(); _relayPolicies.Clear(); _removedTargets.Clear();
            _measureSchemas.Clear(); _methodSchemas.Clear();
            _members.Clear(); _retiredPeers.Clear(); _heldBroadcastPeers.Clear(); RefreshMembersLocked();
            waiters = _pending.Values.ToArray();
            _pending.Clear(); _relays.Clear(); _fanout.Clear();
        }
        try { _lifetime.Cancel(); if (_datagrams != null) _datagrams.UnreliableReceived -= ReceiveUnreliable;
            if (_memory != null) _memory.MemoryReceived -= ReceiveMemory;
            if (_wire != null) { if (_memory == null) _wire.Received -= Receive; _wire.PeerLeft -= Left; _wire.Dispose(); } }
        finally { foreach (var waiter in waiters) waiter.Completion.TrySetException(new RpcException(RpcError.Disposed, "Scope disposed")); }
    }
    private void Check() { if (Volatile.Read(ref _disposed) != 0) throw new RpcException(RpcError.Disposed, "Scope disposed"); if (!IsConnected) throw new RpcException(RpcError.Disconnected, "Transport disconnected"); }
    private void RequireHost() { if (!IsHost) throw new RpcException(RpcError.InvalidRole, "Host authority required"); }
    public static string MethodId(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var signature = method.Name + "(" + string.Join(",", parameters.Select(p => TypeId(p.ParameterType))) + "):" + TypeId(method.ReturnType);
        var type = method.DeclaringType!;
        return method.GetCustomAttribute<WovenRpcAttribute>()?.Id ?? TypeId(type) + "/" + signature;
    }
    public static string StableHash(string text)
    { unchecked { uint hash = 2166136261; foreach (var c in text) { hash ^= c; hash *= 16777619; } return hash.ToString("x8"); } }
    private static string TypeId(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeId)) + ">" : type.FullName!;
}

public static class DispatchBridge
{
    private sealed class Binding { public RpcRuntime Runtime = null!; public TargetKey Key; public object Token = null!; }
    private static readonly ConditionalWeakTable<object, Binding> Bindings = new();
    private static readonly ConditionalWeakTable<object, object> Detached = new();
    internal static bool Attach(object instance, RpcRuntime runtime, TargetKey key, object token)
    { lock (Bindings) { if (Bindings.TryGetValue(instance, out _)) return false; Detached.Remove(instance); Bindings.Add(instance, new Binding { Runtime = runtime, Key = key, Token = token }); return true; } }
    internal static void Detach(object instance, RpcRuntime runtime)
    { lock (Bindings) { if (Bindings.TryGetValue(instance, out var b) && ReferenceEquals(b.Runtime, runtime)) { Bindings.Remove(instance); Detached.GetValue(instance, _ => new object()); } } }
    private static Binding Get(object instance) => Bindings.TryGetValue(instance, out var b) ? b : throw new RpcException(RpcError.Disposed, "Unbound object");
    public static object? Invoke(object instance, string method, object?[] args, SendTo to, Type returnType)
    { var b = Get(instance); return b.Runtime.Dispatch(instance, b.Key, b.Token, method, args, to, returnType); }
    public static TypedRpcWriter BeginTyped(object instance, ulong method, ulong fingerprint, SendTo to, RpcDelivery delivery, RpcTarget target)
    { var b = Get(instance); return b.Runtime.BeginTyped(instance, b.Key, b.Token, method, fingerprint, to, delivery, target); }
    public static void Set(object instance, string property, object? value)
    {
        if (Bindings.TryGetValue(instance, out var b)) { b.Runtime.SetState(instance, b.Key, b.Token, property, value); return; }
        if (Detached.TryGetValue(instance, out _)) throw new RpcException(RpcError.Disposed, "Detached state object");
        // Before first bind, explicit constructor/property initialization is a local auto-property write.
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var raw = type.GetMethod("__bitkit_set_" + property, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (raw == null) continue;
            try { raw.Invoke(instance, new[] { value }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            return;
        }
        throw new RpcException(RpcError.MissingMethod, "Missing woven state setter: " + property);
    }
    public static void Guard(object instance, bool host)
    { var b = Get(instance); if (b.Runtime.IsHost != host) throw new RpcException(RpcError.InvalidRole, "Wrong role"); }
}

public class RemoteProxy<T> : DispatchProxy where T : class
{
    private RpcRuntime _runtime = null!;
    private TargetKey _key;
    private IReadOnlyDictionary<string, SendTo> _routes = null!;
    private IReadOnlyDictionary<string, RpcDelivery> _deliveries = null!;
    internal void Initialize(RpcRuntime runtime, TargetKey key, IReadOnlyDictionary<string, SendTo> routes, IReadOnlyDictionary<string, RpcDelivery> deliveries)
    { _runtime = runtime; _key = key; _routes = routes; _deliveries = deliveries; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null || !_routes.TryGetValue(RpcRuntime.MethodId(targetMethod), out var to)) throw new RpcException(RpcError.MissingMethod, "Unconfigured proxy method");
        var id = RpcRuntime.MethodId(targetMethod);
        return _runtime.DispatchProxy(_key, targetMethod, args ?? Array.Empty<object>(), to,
            _deliveries.TryGetValue(id, out var delivery) ? delivery : null);
    }
}
}
