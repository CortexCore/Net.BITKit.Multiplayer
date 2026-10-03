using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MemoryPack;

namespace BITKit.Multiplayer
{
// Bounds checker for the explicitly supported generated MemoryPack schemas. This does
// not implement value decoding: only MemoryPackSerializer constructs business objects.
// No union, polymorphism, custom formatter, callback, version-tolerant or cyclic DTOs.
internal static class ReliableValues
{
    internal const int MaxState = 16384, MaxArray = 256, MaxDepth = 32, MaxStringChars = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<Type, Schema> Schemas = new();
    private sealed class Schema
    {
        internal int FixedSize;
        internal Type[]? Members;
        internal Type? Element;
        internal byte Kind; // 0 fixed, 1 string, 2 collection, 3 generated DTO
    }
    internal static void Check(Type type) { if (!Schemas.ContainsKey(type)) _ = Get(type, new HashSet<Type>(), 0); }
    internal static void CheckStateSchema(Type type)
    {
        Check(type);
        CheckStateSchemaCore(type);
    }
    private static void CheckStateSchemaCore(Type type)
    {
        var schema = Get(type);
        if (schema.Kind == 2) throw new RpcException(RpcError.InvalidPayload, "SyncVar collections are unsupported, including nested DTO collections");
        if (schema.Members != null) foreach (var member in schema.Members) CheckStateSchemaCore(member);
    }
    private static Schema Get(Type type, HashSet<Type> path, int depth)
    {
        if (Schemas.TryGetValue(type, out var cached)) return cached;
        if (depth > MaxDepth || !path.Add(type)) throw new RpcException(RpcError.InvalidPayload, "Cyclic or overly deep reliable schema " + type);
        Schema schema;
        try
        {
            if (type == typeof(string)) schema = new Schema { Kind = 1 };
            else if (type.IsArray && type.GetArrayRank() == 1 || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ArraySegment<>))
            {
                var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
                Get(element, path, depth + 1);
                schema = new Schema { Kind = 2, Element = element };
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                // Nullable unmanaged scalars have no nested lengths or attacker-sized allocations.
                var element = type.GetGenericArguments()[0];
                var nested = Get(element, path, depth + 1);
                if (nested.Kind != 0) throw new RpcException(RpcError.InvalidPayload, "Nullable reference-bearing reliable schema is unsupported");
                schema = new Schema { Kind = 0, FixedSize = SizeOf(type) };
            }
            else if (type.IsPrimitive || type.IsEnum || type.IsValueType && IsUnmanaged(type))
            {
                if (type.IsValueType && !type.IsEnum && type.IsExplicitLayout)
                    throw new RpcException(RpcError.InvalidPayload, "Explicit unmanaged layouts are not supported by typed RPC schema");
                if (type.GetCustomAttributesData().Any(a => a.AttributeType == typeof(MemoryPackableAttribute) &&
                        (a.ConstructorArguments.Count != 1 || Convert.ToInt32(a.ConstructorArguments[0].Value) != 0 || a.NamedArguments.Count != 0) ||
                        a.AttributeType.Name.Contains("MemoryPackUnion")) ||
                    type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Any(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name.StartsWith("MemoryPackOn", StringComparison.Ordinal) ||
                            a.AttributeType.Name.Contains("Formatter") || a.AttributeType.Name is
                                "MemoryPackIncludeAttribute" or "MemoryPackIgnoreAttribute" or "MemoryPackAllowSerializeAttribute")))
                    throw new RpcException(RpcError.InvalidPayload, "Unsupported unmanaged MemoryPack schema " + type);
                schema = new Schema { Kind = 0, FixedSize = SizeOf(type) };
            }
            else
            {
                var marker = type.GetCustomAttribute<MemoryPackableAttribute>();
                if (marker == null || type.IsAbstract || type.IsInterface || type.BaseType != typeof(object) && !type.IsValueType ||
                    type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Any(c => c.GetParameters().Length != 0 || c.GetCustomAttributesData().Any(a => a.AttributeType.Name == "MemoryPackConstructorAttribute")) ||
                    type.GetCustomAttributesData().Any(a => a.AttributeType.Name is "MemoryPackUnionAttribute" or "MemoryPackAllowSerializeAttribute" ||
                        a.AttributeType == typeof(MemoryPackableAttribute) &&
                            (a.ConstructorArguments.Count != 1 || Convert.ToInt32(a.ConstructorArguments[0].Value) != 0 || a.NamedArguments.Count != 0)) ||
                    type.GetMembers(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name.StartsWith("MemoryPackOn", StringComparison.Ordinal) ||
                            a.AttributeType.Name.Contains("Formatter") || a.AttributeType.Name is
                                "MemoryPackAllowSerializeAttribute" or "MemoryPackIncludeAttribute" or "MemoryPackIgnoreAttribute")))
                    throw new RpcException(RpcError.InvalidPayload, "Unsupported reliable MemoryPack schema " + type);
                var members = type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m is FieldInfo f && !f.IsStatic || m is PropertyInfo p && p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0)
                    .OrderBy(m => m.GetCustomAttribute<MemoryPackOrderAttribute>()?.Order ?? int.MaxValue)
                    .ToArray();
                if (members.Length == 0 || members.Length > 254) throw new RpcException(RpcError.LimitExceeded, "Reliable DTO member limit exceeded");
                for (int i = 0; i < members.Length; i++)
                    if (members[i].GetCustomAttribute<MemoryPackOrderAttribute>()?.Order != i)
                        throw new RpcException(RpcError.InvalidPayload, "Reliable DTO requires contiguous explicit MemoryPackOrder: " + type);
                var types = members.Select(m => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType).ToArray();
                foreach (var member in types) Get(member, path, depth + 1);
                schema = new Schema { Kind = 3, Members = types };
            }
            Schemas.TryAdd(type, schema);
            return schema;
        }
        finally { path.Remove(type); }
    }
    private static Schema Get(Type type) => Schemas.TryGetValue(type, out var cached) ? cached : Get(type, new HashSet<Type>(), 0);
    private static readonly MethodInfo SizeOfMethod = typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Unsafe.SizeOf) && m.IsGenericMethodDefinition);
    private static readonly MethodInfo ContainsMethod = typeof(RuntimeHelpers).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(RuntimeHelpers.IsReferenceOrContainsReferences) && m.IsGenericMethodDefinition);
    private static int SizeOf(Type type) => (int)SizeOfMethod.MakeGenericMethod(type).Invoke(null, null)!;
    private static bool IsUnmanaged(Type type) => !(bool)ContainsMethod.MakeGenericMethod(type).Invoke(null, null)!;

    internal static byte[] Encode(Type type, object? value, int max = ReliableCodec.MaxFrame)
    {
        Check(type);
        using var writer = new BinaryBufferWriter(max);
        try { MemoryPackSerializer.Serialize(type, writer, value); }
        catch (Exception ex) when (ex is not RpcException) { throw new RpcException(RpcError.InvalidPayload, "Unsupported reliable value: " + ex.Message); }
        var bytes = writer.CopyOwned();
        Preflight(type, bytes, max);
        return bytes;
    }
    internal static object? Decode(Type type, byte[]? bytes, int max = ReliableCodec.MaxFrame)
    {
        if (bytes == null) throw new RpcException(RpcError.InvalidPayload, "Missing typed reliable value");
        Preflight(type, bytes, max);
        object? value = null;
        try
        {
            var used = MemoryPackSerializer.Deserialize(type, bytes.AsSpan(), ref value);
            if (used != bytes.Length) throw new RpcException(RpcError.InvalidPayload, "Trailing reliable value bytes");
        }
        catch (Exception ex) when (ex is not RpcException) { throw new RpcException(RpcError.InvalidPayload, "Invalid typed reliable value: " + ex.Message); }
        if (value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            throw new RpcException(RpcError.InvalidPayload, "Null for non-nullable " + type);
        return value;
    }
    internal static void Preflight(Type type, ReadOnlySpan<byte> bytes, int max = ReliableCodec.MaxFrame)
    {
        if (bytes.Length > max) throw new RpcException(RpcError.LimitExceeded, "Reliable value exceeds limit");
        var reader = new BoundsReader(bytes);
        int budget = 4096;
        Walk(type, ref reader, 0, ref budget);
        if (reader.Remaining != 0) throw new RpcException(RpcError.InvalidPayload, "Trailing reliable value data");
    }
    private static void Walk(Type type, ref BoundsReader reader, int depth, ref int budget)
    {
        if (depth > MaxDepth || --budget < 0) throw new RpcException(RpcError.LimitExceeded, "Reliable value nesting/element budget exceeded");
        var schema = Get(type);
        if (schema.Kind == 0) { reader.Skip(schema.FixedSize); return; }
        if (schema.Kind == 1)
        {
            var length = reader.Int();
            if (length == -1) return;
            if (length >= 0)
            {
                if (length > MaxStringChars) throw new RpcException(RpcError.LimitExceeded, "Reliable UTF16 string exceeds character cap");
                reader.Skip(checked((long)length * 2)); return;
            }
            long utf8 = ~(long)length;
            if (utf8 > reader.Remaining - 4L || utf8 > MaxStringChars * 4L) throw new RpcException(RpcError.InvalidPayload, "Invalid reliable UTF8 length");
            var charCount = reader.Int();
            if (charCount < 0 || charCount > MaxStringChars) throw new RpcException(RpcError.LimitExceeded, "Reliable string character count exceeds cap");
            int actual;
            try { actual = StrictUtf8.GetCharCount(reader.Take((int)utf8)); }
            catch (DecoderFallbackException) { throw new RpcException(RpcError.InvalidPayload, "Malformed reliable UTF8 string"); }
            if (actual > MaxStringChars || charCount != 0 && charCount != actual)
                throw new RpcException(RpcError.InvalidPayload, "Reliable UTF8 character count mismatch");
            return;
        }
        if (schema.Kind == 2)
        {
            var count = reader.Int();
            if (count == -1) return;
            if (count < 0 || count > MaxArray || count > reader.Remaining) throw new RpcException(RpcError.LimitExceeded, "Reliable array/segment count exceeds limit");
            budget -= count;
            if (budget < 0) throw new RpcException(RpcError.LimitExceeded, "Reliable cumulative collection budget exceeded");
            for (int i = 0; i < count; i++) Walk(schema.Element!, ref reader, depth + 1, ref budget);
            return;
        }
        var memberCount = reader.Byte();
        if (memberCount == 255) return;
        if (memberCount > schema.Members!.Length) throw new RpcException(RpcError.InvalidPayload, "Unsupported reliable DTO member count");
        // Default MemoryPack object layout permits appending members (older sender).
        for (int i = 0; i < memberCount; i++) Walk(schema.Members[i], ref reader, depth + 1, ref budget);
    }
    private ref struct BoundsReader
    {
        private ReadOnlySpan<byte> _bytes;
        internal BoundsReader(ReadOnlySpan<byte> bytes) => _bytes = bytes;
        internal int Remaining => _bytes.Length;
        internal void Skip(long count)
        { if (count < 0 || count > _bytes.Length) throw new RpcException(RpcError.InvalidPayload, "Truncated reliable value"); _bytes = _bytes.Slice((int)count); }
        internal ReadOnlySpan<byte> Take(int count)
        { if (count < 0 || count > _bytes.Length) throw new RpcException(RpcError.InvalidPayload, "Truncated reliable value"); var value = _bytes.Slice(0, count); Skip(count); return value; }
        internal byte Byte() { if (_bytes.IsEmpty) throw new RpcException(RpcError.InvalidPayload, "Truncated reliable value"); var x = _bytes[0]; Skip(1); return x; }
        internal int Int() { if (_bytes.Length < 4) throw new RpcException(RpcError.InvalidPayload, "Truncated reliable value"); var x = BinaryPrimitives.ReadInt32LittleEndian(_bytes); Skip(4); return x; }
    }
}
}
