using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace BITKit.Multiplayer
{
// Reliable room protocol v3. The byte[] returned to IRoomWire is owned until its
// SendAsync completes; it is NOT rented/recycled under an asynchronous transport.
internal static class ReliableCodec
{
    internal const int MaxFrame = 1048576;
    private const byte Magic = 0xB5, Version = 3;
    private const int MaxArguments = 32, MaxMembers = 256, MaxTargets = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Kinds = { "", "call", "reply", "state", "snapshotRequest", "members", "membersRequest", "memberRevoked", "targetRemoved", "stateDelta", "timeRequest", "timeReply" };

    internal static byte[] Encode(Packet packet)
    {
        using var writer = BinaryBufferWriter.Rent(MaxFrame);
        PutByte(writer, Magic); PutByte(writer, Version);
        var kind = Array.IndexOf(Kinds, packet.Kind);
        if (kind <= 0) throw new RpcException(RpcError.InvalidPayload, "Unknown reliable packet kind");
        PutByte(writer, (byte)kind); PutByte(writer, checked((byte)packet.To));
        PutByte(writer, (byte)((byte)packet.Error | (packet.OneWay ? 0x80 : 0))); PutLong(writer, packet.Version);
        PutString(writer, packet.Scope, 256); PutString(writer, packet.Id, 128);
        PutString(writer, packet.Origin, 128); PutString(writer, packet.Requester, 128);
        PutString(writer, packet.Destination, 128); PutString(writer, packet.Service, 256);
        PutString(writer, packet.Entity, 256); PutString(writer, packet.Component, 256);
        PutString(writer, packet.Method, 1024); PutString(writer, packet.Property, 256);
        PutString(writer, packet.Fault, 4096);
        PutBlobs(writer, packet.Args);
        PutBlob(writer, packet.Result); PutBlob(writer, packet.Value);
        if (packet.Members != null && packet.Members.Count > MaxMembers || packet.RemovedTargets != null && packet.RemovedTargets.Count > MaxTargets)
            throw new RpcException(RpcError.LimitExceeded, "Reliable directory limit exceeded");
        PutShort(writer, packet.Members == null ? ushort.MaxValue : checked((ushort)packet.Members.Count));
        if (packet.Members != null)
            foreach (var member in packet.Members)
            {
                PutString(writer, member.Peer, 128); PutString(writer, member.PlayerId, 256);
                PutString(writer, member.SteamId, 256); PutByte(writer, member.Ready ? (byte)1 : (byte)0);
            }
        PutShort(writer, packet.RemovedTargets == null ? ushort.MaxValue : checked((ushort)packet.RemovedTargets.Count));
        if (packet.RemovedTargets != null)
            foreach (var removed in packet.RemovedTargets)
            { PutString(writer, removed.Service, 256); PutString(writer, removed.Entity, 256); PutString(writer, removed.Component, 256); }
        return writer.CopyOwned();
    }
    internal static Packet Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFrame) throw new RpcException(RpcError.LimitExceeded, "Reliable frame exceeds 1 MiB");
        var r = new Reader(bytes);
        if (r.Byte() != Magic || r.Byte() != Version) throw new RpcException(RpcError.InvalidPayload, "Unsupported reliable envelope/version (binary v3 required)");
        var kind = r.Byte();
        if (kind == 0 || kind >= Kinds.Length) throw new RpcException(RpcError.InvalidPayload, "Unknown reliable packet kind");
        var route = (SendTo)r.Byte(); var error = r.Byte();
        var packet = new Packet { V = Version, Kind = Kinds[kind], To = route, Error = (RpcError)(error & 0x7f), OneWay = (error & 0x80) != 0, Version = r.Long(),
            Scope = r.String(256) ?? "", Id = r.String(128) ?? "", Origin = r.String(128) ?? "",
            Requester = r.String(128) ?? "", Destination = r.String(128) ?? "", Service = r.String(256) ?? "",
            Entity = r.String(256) ?? "", Component = r.String(256) ?? "", Method = r.String(1024) ?? "",
            Property = r.String(256), Fault = r.String(4096), Args = r.Blobs(), Result = r.Blob(), Value = r.Blob() };
        var count = r.Short();
        if (count != ushort.MaxValue)
        {
            if (count > MaxMembers || count > r.Remaining / 7) throw new RpcException(RpcError.LimitExceeded, "Invalid member count");
            packet.Members = new List<MemberRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var member = new MemberRecord { Peer = r.String(128) ?? "", PlayerId = r.String(256), SteamId = r.String(256) };
                var ready = r.Byte();
                if (ready > 1) throw new RpcException(RpcError.InvalidPayload, "Invalid member readiness");
                member.Ready = ready == 1; packet.Members.Add(member);
            }
        }
        count = r.Short();
        if (count != ushort.MaxValue)
        {
            if (count > MaxTargets || count > r.Remaining / 6) throw new RpcException(RpcError.LimitExceeded, "Invalid tombstone count");
            packet.RemovedTargets = new List<TargetRecord>(count);
            for (int i = 0; i < count; i++) packet.RemovedTargets.Add(new TargetRecord
            { Service = r.String(256) ?? "", Entity = r.String(256) ?? "", Component = r.String(256) ?? "" });
        }
        if (r.Remaining != 0) throw new RpcException(RpcError.InvalidPayload, "Trailing reliable envelope data");
        return packet;
    }
    private static void PutByte(BinaryBufferWriter w, byte x) { w.GetSpan(1)[0] = x; w.Advance(1); }
    private static void PutShort(BinaryBufferWriter w, ushort x) { BinaryPrimitives.WriteUInt16LittleEndian(w.GetSpan(2), x); w.Advance(2); }
    private static void PutInt(BinaryBufferWriter w, int x) { BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), x); w.Advance(4); }
    private static void PutLong(BinaryBufferWriter w, long x) { BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(8), x); w.Advance(8); }
    private static void PutString(BinaryBufferWriter w, string? text, int maxCharacters)
    {
        if (text == null) { PutShort(w, ushort.MaxValue); return; }
        if (text.Length > maxCharacters) throw new RpcException(RpcError.LimitExceeded, "Reliable routing string too long");
        var length = Utf8.GetByteCount(text);
        PutShort(w, checked((ushort)length));
        w.Advance(Utf8.GetBytes(text.AsSpan(), w.GetSpan(length)));
    }
    private static void PutBlob(BinaryBufferWriter w, byte[]? value)
    {
        if (value == null) { PutInt(w, -1); return; }
        PutInt(w, value.Length); w.Write(value);
    }
    private static void PutBlobs(BinaryBufferWriter w, byte[][]? blobs)
    {
        if (blobs == null) { PutByte(w, byte.MaxValue); return; }
        if (blobs.Length > MaxArguments) throw new RpcException(RpcError.LimitExceeded, "Too many reliable arguments");
        PutByte(w, (byte)blobs.Length);
        foreach (var b in blobs) PutBlob(w, b ?? throw new RpcException(RpcError.InvalidPayload, "Missing argument blob"));
    }
    private ref struct Reader
    {
        private ReadOnlySpan<byte> _data;
        internal Reader(ReadOnlySpan<byte> bytes) => _data = bytes;
        internal int Remaining => _data.Length;
        internal ReadOnlySpan<byte> Bytes(int count)
        { if (count < 0 || count > _data.Length) throw new RpcException(RpcError.InvalidPayload, "Truncated reliable frame"); var value = _data.Slice(0, count); _data = _data.Slice(count); return value; }
        internal byte Byte() => Bytes(1)[0];
        internal ushort Short() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        internal int Int() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        internal long Long() => BinaryPrimitives.ReadInt64LittleEndian(Bytes(8));
        internal string? String(int maxChars)
        {
            var count = Short(); if (count == ushort.MaxValue) return null;
            if (count > maxChars * 4) throw new RpcException(RpcError.LimitExceeded, "Oversized reliable routing string");
            var s = Utf8.GetString(Bytes(count));
            if (s.Length > maxChars) throw new RpcException(RpcError.LimitExceeded, "Oversized reliable routing string");
            return s;
        }
        internal byte[]? Blob()
        {
            var count = Int(); if (count == -1) return null;
            if (count < 0 || count > MaxFrame || count > Remaining) throw new RpcException(RpcError.InvalidPayload, "Invalid reliable blob length");
            return Bytes(count).ToArray();
        }
        internal byte[][]? Blobs()
        {
            var count = Byte(); if (count == byte.MaxValue) return null;
            if (count > MaxArguments || count > Remaining / 4) throw new RpcException(RpcError.LimitExceeded, "Invalid reliable argument count");
            var args = new byte[count][];
            for (int i = 0; i < count; i++) args[i] = Blob() ?? throw new RpcException(RpcError.InvalidPayload, "Null argument blob");
            return args;
        }
    }
}
}
