using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using MemoryPack;

namespace BITKit.Multiplayer
{
// State payload inside the existing authenticated reliable envelope. All upgraded
// state members carry a schema fingerprint; deltas additionally carry a base revision.
internal static class SyncWire
{
    internal const int Maximum = 16384, MaxItems = 256, MaxOperations = 64, Header = 19;
    internal static ulong Mix(ulong first, ulong second)
    {
        unchecked
        {
            ulong hash = first;
            for (int i = 0; i < 8; i++) { hash ^= (byte)(second >> (i * 8)); hash *= 1099511628211UL; }
            return hash;
        }
    }
    internal static ulong ContractFingerprint(IReadOnlyDictionary<string, ulong> members)
    {
        ulong hash = Mix(14695981039346656037UL, (ulong)members.Count);
        foreach (var pair in members.OrderBy(p => p.Key, StringComparer.Ordinal)) hash = Mix(hash, pair.Value);
        return hash;
    }
    internal static byte[] Wrap(byte kind, ulong fingerprint, long basis, byte[] body)
    {
        if (body.Length > Maximum - Header) throw new RpcException(RpcError.LimitExceeded, "Synchronized value exceeds 16 KiB");
        var frame = new byte[Header + body.Length];
        frame[0] = 0x53; frame[1] = 1; frame[2] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3), fingerprint);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(11), basis);
        body.CopyTo(frame, Header); return frame;
    }
    internal static void HeaderTo(BinaryBufferWriter writer, byte kind, ulong fingerprint, long basis)
    {
        var header = writer.GetSpan(Header);
        header[0] = 0x53; header[1] = 1; header[2] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(3), fingerprint);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(11), basis);
        writer.Advance(Header);
    }
    internal static ReadOnlySpan<byte> Open(byte[] frame, byte kind, ulong fingerprint, out long basis)
    {
        if (frame.Length < Header || frame.Length > Maximum || frame[0] != 0x53 || frame[1] != 1 || frame[2] != kind)
            throw new RpcException(RpcError.InvalidPayload, "Unsupported synchronized state format/kind");
        if (BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(3)) != fingerprint)
            throw new RpcException(RpcError.InvalidPayload, "SyncVar schema fingerprint mismatch");
        basis = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(11));
        return frame.AsSpan(Header);
    }
    internal static void Int(BinaryBufferWriter writer, int value)
    { BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value); writer.Advance(4); }
    internal static void Byte(BinaryBufferWriter writer, byte value)
    { writer.GetSpan(1)[0] = value; writer.Advance(1); }
    internal static int ReadInt(ref ReadOnlySpan<byte> input)
    { var bytes = Take(ref input, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
    internal static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> input, int length)
    {
        if (length < 0 || length > input.Length) throw new RpcException(RpcError.InvalidPayload, "Truncated collection data");
        var result = input.Slice(0, length); input = input.Slice(length); return result;
    }
    internal static int Count(ref ReadOnlySpan<byte> input, int maximum)
    {
        int count = ReadInt(ref input);
        if (count < 0 || count > maximum) throw new RpcException(RpcError.LimitExceeded, "Invalid collection count");
        return count;
    }
    internal static void End(ReadOnlySpan<byte> input)
    { if (!input.IsEmpty) throw new RpcException(RpcError.InvalidPayload, "Trailing collection data"); }
}
internal static class SyncValue<T>
{
    private static readonly bool NeedsCopy = typeof(T) != typeof(string) && RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    private static readonly int FixedSize = RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 0 : Unsafe.SizeOf<T>();
    internal static T Copy(T value)
    {
        if (!NeedsCopy || value is null) return value;
        using var writer = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        EncodeTo(writer, value); T? copy = default;
        MemoryPackSerializer.Deserialize(writer.WrittenSpan, ref copy); return copy!;
    }
    internal static bool Equal(T a, T b)
    {
        if (!NeedsCopy) return EqualityComparer<T>.Default.Equals(a, b);
        using var first = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        using var second = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        EncodeTo(first, a); EncodeTo(second, b);
        return first.WrittenSpan.SequenceEqual(second.WrittenSpan);
    }
    internal static void Check(bool key = false)
    {
        ReliableValues.CheckStateSchema(typeof(T));
        if (key && !(typeof(T).IsPrimitive || typeof(T).IsEnum || typeof(T) == typeof(string)))
            throw new RpcException(RpcError.InvalidPayload, "Collection keys/set elements require primitive, enum or string with default equality");
    }
    private static void EncodeTo(BinaryBufferWriter writer, T value)
    {
        int start = writer.WrittenCount;
        MemoryPackSerializer.Serialize(writer, value);
        if (FixedSize == 0) ReliableValues.Preflight(typeof(T), writer.WrittenSpan.Slice(start), SyncWire.Maximum);
    }
    internal static int Size(T value)
    {
        if (FixedSize != 0) return 4 + FixedSize;
        using var writer = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        EncodeTo(writer, value); return 4 + writer.WrittenCount;
    }
    internal static void Write(BinaryBufferWriter writer, T value)
    {
        int offset = writer.WrittenCount;
        SyncWire.Int(writer, 0); EncodeTo(writer, value);
        writer.PatchInt32(offset, writer.WrittenCount - offset - 4);
    }
    internal static T Read(ref ReadOnlySpan<byte> input)
    {
        var bytes = SyncWire.Take(ref input, SyncWire.ReadInt(ref input));
        ReliableValues.Preflight(typeof(T), bytes, SyncWire.Maximum);
        T? value = default;
        if (MemoryPackSerializer.Deserialize(bytes, ref value) != bytes.Length)
            throw new RpcException(RpcError.InvalidPayload, "Trailing collection element");
        return value!;
    }
}
}
