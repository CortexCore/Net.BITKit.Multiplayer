using System;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace BITKit.Multiplayer.NetRpc
{
    internal static class NetValue<T>
    {
        internal static readonly bool Independent = IsIndependent(typeof(T));
        private static bool IsIndependent(Type type) => type == typeof(string) || type.IsPrimitive || type.IsEnum || type.IsValueType &&
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(f => f.FieldType.IsValueType && IsIndependent(f.FieldType));
        internal static T Copy(T value) => Independent || value == null ? value :
            MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(value, NetSerialization.Options), NetSerialization.Options);
        internal static bool Equal(T a, T b)
        {
            if (Independent) return System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);
            if (a == null || b == null) return a == null && b == null;
            return MessagePackSerializer.Serialize(a, NetSerialization.Options).AsSpan().SequenceEqual(MessagePackSerializer.Serialize(b, NetSerialization.Options));
        }
    }
}
