using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace BITKit.Multiplayer
{
// Build-time Cecil emits wrappers and static receivers. This is a wire header, not
// a replacement for the cold directory/state protocol. Strings never appear in v4.
public readonly struct TypedRpcHeader
{
    public const byte Magic = 0xB6, Version = 4;
    public const int Size = 88;
    public readonly SendTo Route;
    public readonly byte Kind;
    public readonly ulong ScopeA, ScopeB, TargetA, TargetB, Sender, Destination, Method, Fingerprint;
    public readonly Guid Request;
    public readonly int PayloadLength;
    internal TypedRpcHeader(byte kind, SendTo route, ulong scopeA, ulong scopeB, ulong targetA, ulong targetB,
        ulong sender, ulong destination, ulong method, ulong fingerprint, Guid request, int payloadLength)
    {
        Kind = kind; Route = route; ScopeA = scopeA; ScopeB = scopeB; TargetA = targetA; TargetB = targetB;
        Sender = sender; Destination = destination; Method = method; Fingerprint = fingerprint;
        Request = request; PayloadLength = payloadLength;
    }
    internal static bool TryRead(ReadOnlySpan<byte> data, out TypedRpcHeader header)
    {
        header = default;
        if (data.Length < Size || data[0] != Magic || data[1] != Version) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(84, 4));
        if (count < 0 || count > ReliableCodec.MaxFrame - Size || count != data.Length - Size) return false;
        header = new TypedRpcHeader(data[2], (SendTo)data[3],
            U64(data, 4), U64(data, 12), U64(data, 20), U64(data, 28),
            U64(data, 36), U64(data, 44), U64(data, 52), U64(data, 60),
            new Guid(data.Slice(68, 16)), count);
        return data[2] is 1 or 2 or 3 && header.Route is SendTo.Host or SendTo.All or SendTo.Target;
    }
    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));
    internal void Write(Span<byte> data)
    {
        data[0] = Magic; data[1] = Version; data[2] = Kind; data[3] = (byte)Route;
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(4, 8), ScopeA);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(12, 8), ScopeB);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(20, 8), TargetA);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(28, 8), TargetB);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(36, 8), Sender);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(44, 8), Destination);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(52, 8), Method);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(60, 8), Fingerprint);
        Request.TryWriteBytes(data.Slice(68, 16));
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(84, 4), PayloadLength);
    }
}

internal readonly struct TypedTarget : IEquatable<TypedTarget>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal readonly ulong A, B;
    internal TypedTarget(ulong a, ulong b) { A = a; B = b; }
    public bool Equals(TypedTarget other) => A == other.A && B == other.B;
    public override bool Equals(object? value) => value is TypedTarget other && Equals(other);
    public override int GetHashCode() => (A, B).GetHashCode();
    internal static TypedTarget Hash(TargetKey key)
    {
        // Length prefixes, not separators: TargetKey permits embedded NUL characters.
        // A\0B/C/D and A/B\0C/D must never become the same hashed byte stream.
        var fields = new[] { key.Service, key.Entity, key.Component };
        var lengths = new int[fields.Length];
        int total = fields.Length * 4;
        try
        {
            for (int i = 0; i < fields.Length; i++)
            { lengths[i] = StrictUtf8.GetByteCount(fields[i]); total = checked(total + lengths[i]); }
        }
        catch (EncoderFallbackException ex) { throw new RpcException(RpcError.InvalidPayload, "Invalid Unicode target key: " + ex.Message); }
        var encoded = new byte[total]; int offset = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(offset, 4), lengths[i]); offset += 4;
            StrictUtf8.GetBytes(fields[i].AsSpan(), encoded.AsSpan(offset, lengths[i])); offset += lengths[i];
        }
        using var digest = SHA256.Create();
        var bytes = digest.ComputeHash(encoded);
        return new TypedTarget(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
    }
    internal static TypedTarget Scope(string scope)
    {
        byte[] encoded;
        try { encoded = StrictUtf8.GetBytes(scope); }
        catch (EncoderFallbackException ex) { throw new RpcException(RpcError.InvalidPayload, "Invalid Unicode room scope: " + ex.Message); }
        using var digest = SHA256.Create();
        var bytes = digest.ComputeHash(encoded);
        return new TypedTarget(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
    }
    internal static ulong Peer(PeerId peer)
    {
        unchecked { ulong hash = 14695981039346656037UL; foreach (var c in peer.Value) { hash ^= c; hash *= 1099511628211UL; } return hash; }
    }
}

public delegate Task<byte[]?> TypedRpcReceiver(object instance, ref TypedRpcReader reader);

// Only the receiver's synchronous argument decoding accesses borrowed memory. A
// generated Task/Task<T> body may continue after the callback, but its arguments own
// their decoded arrays/DTOs and no reader is captured by the continuation.
public ref struct TypedRpcReader
{
    private ReadOnlySpan<byte> _data;
    private readonly RpcTarget _destination;
    public TypedRpcReader(ReadOnlySpan<byte> payload, RpcTarget destination) { _data = payload; _destination = destination; }
    public RpcTarget Target() => _destination;
    public T Read<T>()
    {
        if (_data.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated typed argument");
        var length = BinaryPrimitives.ReadInt32LittleEndian(_data);
        _data = _data.Slice(4);
        if (length < 0 || length > _data.Length) throw new RpcException(RpcError.InvalidPayload, "Invalid typed argument length");
        var argument = _data.Slice(0, length);
        ReliableValues.Preflight(typeof(T), argument);
        T? result;
        try { result = MemoryPackSerializer.Deserialize<T>(argument); }
        catch (Exception ex) { throw new RpcException(RpcError.InvalidPayload, "Invalid typed argument: " + ex.Message); }
        _data = _data.Slice(length);
        return result!;
    }
    public void Complete()
    { if (!_data.IsEmpty) throw new RpcException(RpcError.InvalidPayload, "Trailing typed arguments"); }
}

// MemoryPack netstandard2.1 requires a reference-type IBufferWriter. Reuse that
// small object from a thread-local bounded Stack; only its rented byte[] crosses
// an async send boundary. No ConcurrentStack node is allocated on return.
public sealed class TypedPacketBuffer : IBufferWriter<byte>
{
    private byte[]? _buffer;
    private int _count;
    private int _limit;
    [ThreadStatic] private static Stack<TypedPacketBuffer>? _cache;
    private static int _active;
    private static int _peak;
    internal static int ActiveRentals => Volatile.Read(ref _active);
    internal static int PeakRentals => Volatile.Read(ref _peak);
    internal static void Return(byte[] bytes) { ArrayPool<byte>.Shared.Return(bytes); Interlocked.Decrement(ref _active); }
    internal static byte[] RentRelay(int length)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        TrackActive();
        return bytes;
    }
    private static void TrackActive()
    {
        var active = Interlocked.Increment(ref _active);
        int peak;
        do { peak = Volatile.Read(ref _peak); if (peak >= active) break; }
        while (Interlocked.CompareExchange(ref _peak, active, peak) != peak);
    }
    internal int Count => _count;
    internal static TypedPacketBuffer Rent(int limit)
    {
        var pool = _cache;
        var writer = pool != null && pool.Count != 0 ? pool.Pop() : new TypedPacketBuffer();
        writer._limit = limit; writer._count = TypedRpcHeader.Size;
        writer._buffer = ArrayPool<byte>.Shared.Rent(128);
        TrackActive();
        return writer;
    }
    internal void Release()
    {
        var bytes = _buffer; _buffer = null;
        if (bytes != null) Return(bytes);
        var pool = _cache ??= new Stack<TypedPacketBuffer>(16);
        if (pool.Count < 64) pool.Push(this);
    }
    internal byte[] Detach()
    {
        var bytes = _buffer ?? throw new ObjectDisposedException(nameof(TypedPacketBuffer));
        _buffer = null;
        return bytes;
    }
    internal Span<byte> Header => _buffer!.AsSpan(0, TypedRpcHeader.Size);
    internal Span<byte> At(int offset, int length) => _buffer!.AsSpan(offset, length);
    internal ReadOnlySpan<byte> Slice(int offset, int length) => _buffer!.AsSpan(offset, length);
    public Span<byte> GetSpan(int hint = 0) { Ensure(hint); return _buffer!.AsSpan(_count); }
    public Memory<byte> GetMemory(int hint = 0) { Ensure(hint); return _buffer!.AsMemory(_count); }
    public void Advance(int count)
    {
        if (_buffer == null || count < 0 || count > _limit - _count || count > _buffer.Length - _count)
            throw new RpcException(RpcError.LimitExceeded, "Typed frame exceeds limit");
        _count += count;
    }
    private void Ensure(int hint)
    {
        if (_buffer == null) throw new ObjectDisposedException(nameof(TypedPacketBuffer));
        int need = Math.Max(1, hint);
        if (need > _limit - _count) throw new RpcException(RpcError.LimitExceeded, "Typed frame exceeds limit");
        if (_buffer.Length - _count >= need) return;
        var replacement = ArrayPool<byte>.Shared.Rent(Math.Min(_limit, Math.Max(_count + need, _buffer.Length * 2)));
        Buffer.BlockCopy(_buffer, 0, replacement, 0, _count);
        Return(_buffer);
        _buffer = replacement;
        TrackActive();
    }
}

// Value-type wrapper ensures finally returns scratch on encode failure. After Finish,
// SendTyped owns the detached byte[] until EVERY fanout ValueTask completes/faults.
public struct TypedRpcWriter : IDisposable
{
    private TypedPacketBuffer? _buffer;
    private RpcCallValueContext _priorContext;
    private bool _localEntered, _localExecuted;
    private bool _slotReserved;
    private readonly RpcRuntime _runtime;
    private readonly TargetKey _key;
    private readonly PeerId _destination;
    private readonly TypedRpcHeader _header;
    public int LengthBytes => _buffer?.Count ?? 0;
    internal TypedRpcWriter(RpcRuntime runtime, TargetKey key, PeerId destination, TypedRpcHeader header, int limit)
    {
        _runtime = runtime; _key = key; _destination = destination; _header = header;
        _buffer = TypedPacketBuffer.Rent(limit);
        _priorContext = default; _localEntered = false; _localExecuted = false; _slotReserved = false;
    }
    public void PrepareVoid()
    {
        if (_runtime.NeedsTypedRemote(_header.Route, _destination))
        { _runtime.ReserveTypedSlot(); _slotReserved = true; }
    }
    public bool ShouldExecuteLocal() => _header.Route == SendTo.All ||
        _destination.Equals(_runtime.LocalPeerId) && (_header.Route == SendTo.Host || _header.Route == SendTo.Target);
    public void EnterLocal()
    {
        if (_localEntered) throw new RpcException(RpcError.InvalidPayload, "Nested local entry on same writer");
        _priorContext = _runtime.EnterTypedLocal(_key, _header.Method, _header.Fingerprint, _header.Route);
        _localEntered = true;
    }
    public void ExitLocal()
    {
        if (!_localEntered) throw new RpcException(RpcError.InvalidPayload, "Local typed context not entered");
        RpcCallContext.ExitValue(_priorContext);
        _localEntered = false; _localExecuted = true;
    }
    public void Write<T>(T value)
    {
        var writer = _buffer ?? throw new ObjectDisposedException(nameof(TypedRpcWriter));
        var lengthAt = writer.Count;
        writer.GetSpan(4).Slice(0, 4).Clear(); writer.Advance(4);
        try { MemoryPackSerializer.Serialize<T, TypedPacketBuffer>(in writer, in value); }
        catch (Exception ex) when (ex is not RpcException) { throw new RpcException(RpcError.InvalidPayload, "Typed argument encoder: " + ex.Message); }
        ReliableValues.Preflight(typeof(T), writer.Slice(lengthAt + 4, writer.Count - lengthAt - 4));
        BinaryPrimitives.WriteInt32LittleEndian(writer.At(lengthAt, 4), writer.Count - lengthAt - 4);
    }
    // Explicit reflection/measurement API only. Generated send wrappers never call this.
    public void WriteBoxed(Type type, object? value)
    {
        var writer = _buffer ?? throw new ObjectDisposedException(nameof(TypedRpcWriter));
        var lengthAt = writer.Count;
        writer.GetSpan(4).Slice(0, 4).Clear(); writer.Advance(4);
        try { MemoryPackSerializer.Serialize(type, writer, value); }
        catch (Exception ex) when (ex is not RpcException) { throw new RpcException(RpcError.InvalidPayload, "Typed measurement encoder: " + ex.Message); }
        ReliableValues.Preflight(type, writer.Slice(lengthAt + 4, writer.Count - lengthAt - 4));
        BinaryPrimitives.WriteInt32LittleEndian(writer.At(lengthAt, 4), writer.Count - lengthAt - 4);
    }
    private (byte[] Bytes, int Count) Take(Guid id)
    {
        var writer = _buffer ?? throw new ObjectDisposedException(nameof(TypedRpcWriter));
        var count = writer.Count;
        var header = new TypedRpcHeader(_header.Kind, _header.Route, _header.ScopeA, _header.ScopeB,
            _header.TargetA, _header.TargetB, _header.Sender, _header.Destination,
            _header.Method, _header.Fingerprint, id, count - TypedRpcHeader.Size);
        header.Write(writer.Header);
        var bytes = writer.Detach();
        _buffer = null;
        writer.Release();
        return (bytes, count);
    }
    public void FinishVoid()
    {
        var item = Take(Guid.Empty);
        var reserved = _slotReserved; _slotReserved = false;
        _runtime.SendTypedVoid(_key, _destination, item.Bytes, item.Count, _localExecuted, reserved);
    }
    public Task FinishTask() { var item = Take(Guid.NewGuid()); return _runtime.SendTypedTask(_key, _destination, item.Bytes, item.Count); }
    public Task<T> FinishTask<T>() { var item = Take(Guid.NewGuid()); return _runtime.SendTypedTask<T>(_key, _destination, item.Bytes, item.Count); }
    public void Dispose()
    {
        if (_localEntered) { RpcCallContext.ExitValue(_priorContext); _localEntered = false; }
        if (_slotReserved) { _runtime.ReleaseTypedSlot(); _slotReserved = false; }
        var writer = _buffer; _buffer = null;
        writer?.Release();
    }
}

public static class TypedRpcResults
{
    public static readonly Task<byte[]?> Void = Task.FromResult<byte[]?>(null);
    public static Task<byte[]?> CompleteTask(Task task) => Await(task);
    private static async Task<byte[]?> Await(Task task) { await task.ConfigureAwait(false); return null; }
    public static Task<byte[]?> CompleteTask<T>(Task<T> task) => Await(task);
    private static async Task<byte[]?> Await<T>(Task<T> task)
    {
        var value = await task.ConfigureAwait(false);
        using var output = new BinaryBufferWriter(ReliableCodec.MaxFrame);
        MemoryPackSerializer.Serialize<T, BinaryBufferWriter>(in output, in value);
        return output.CopyOwned();
    }
}
}
