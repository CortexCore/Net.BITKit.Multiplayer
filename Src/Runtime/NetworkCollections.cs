using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BITKit.Multiplayer.NetRpc
{
    public enum NetworkOperation : byte { Add = 1, Insert = 2, Set = 3, Remove = 4, Clear = 5, Snapshot = 6 }
    public readonly struct NetworkChange
    {
        public NetworkChange(NetworkOperation operation, long revision, int index = -1)
        { Operation = operation; Revision = revision; Index = index; }
        public NetworkOperation Operation { get; }
        public long Revision { get; }
        public int Index { get; }
    }
    internal interface INetworkState
    {
        ulong Schema { get; }
        long Revision { get; }
        void Bind(bool authority, Action<INetworkState, NetMessageBag> publish);
        NetMessageBag Snapshot();
        bool Apply(NetMessageReader reader, bool snapshot, long revision);
    }
    public abstract class NetworkCollection
    {
        public const int MaximumCount = 4096;
        protected readonly object Gate = new();
        protected bool Authority = true;
        private protected Action<INetworkState, NetMessageBag>? Publish;
        protected bool Initialized;
        public long Revision { get; protected set; }
        public event Action<NetworkChange>? Changed;
        protected void CheckWrite() { if (!Authority) throw new RpcException(RpcError.InvalidRole, "Client cannot modify Host-authoritative state; request an RPC."); }
        protected void Notify(NetworkChange change) => Changed?.Invoke(change);
    }
    public sealed class NetworkList<T> : NetworkCollection, IList<T>, IReadOnlyList<T>, INetworkState
    {
        private List<T> _items = new();
        private List<T>? _spare;
        ulong INetworkState.Schema => StateSchema.For(typeof(IList<T>));
        public int Count { get { lock (Gate) return _items.Count; } }
        public bool IsReadOnly => !Authority;
        public T this[int index] { get { lock (Gate) return NetValue<T>.Copy(_items[index]); } set => Change(NetworkOperation.Set, index, value); }
        public void Add(T item) => Change(NetworkOperation.Add, -1, item);
        public void Insert(int index, T item) => Change(NetworkOperation.Insert, index, item);
        public void RemoveAt(int index) => Change(NetworkOperation.Remove, index, default!);
        public void Clear() => Change(NetworkOperation.Clear, -1, default!);
        public bool Remove(T item)
        {
            return Change(NetworkOperation.Remove, -2, item);
        }
        private int FindIndex(T item) { for (int i = 0; i < _items.Count; i++) if (NetValue<T>.Equal(_items[i], item)) return i; return -1; }
        public int IndexOf(T item) { lock (Gate) return FindIndex(item); }
        public bool Contains(T item) => IndexOf(item) >= 0;
        public void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            lock (Gate) { if (arrayIndex < 0 || arrayIndex > array.Length || _items.Count > array.Length - arrayIndex) throw new ArgumentOutOfRangeException(nameof(arrayIndex)); for (int i = 0; i < _items.Count; i++) array[arrayIndex + i] = NetValue<T>.Copy(_items[i]); }
        }
        public IEnumerator<T> GetEnumerator() { lock (Gate) return _items.Select(NetValue<T>.Copy).ToList().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private bool Change(NetworkOperation operation, int index, T value)
        {
            NetMessageBag? delta = null; NetworkChange change;
            lock (Gate)
            {
                CheckWrite(); if (operation == NetworkOperation.Add) index = _items.Count;
                if (operation == NetworkOperation.Remove && index == -2) { index = FindIndex(value); if (index < 0) return false; }
                if (operation == NetworkOperation.Clear && _items.Count == 0 || operation == NetworkOperation.Set && NetValue<T>.Equal(_items[index], value)) return false;
                value = NetValue<T>.Copy(value);
                if (Revision == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Revision exhausted.");
                delta = NetMessageBag.Pool();
                try
                {
                    delta.Write(Revision + 1); delta.Write((byte)operation); delta.Write(index);
                    if (operation == NetworkOperation.Add || operation == NetworkOperation.Insert || operation == NetworkOperation.Set) delta.Write(value);
                    ApplyOperation(_items, operation, index, value);
                    change = new NetworkChange(operation, ++Revision, index);
                }
                catch { delta.Dispose(); throw; }
            }
            try { Publish?.Invoke(this, delta); } finally { delta.Dispose(); }
            Notify(change); return true;
        }
        private static void ApplyOperation(List<T> list, NetworkOperation op, int index, T value)
        {
            switch (op)
            {
                case NetworkOperation.Add:
                    if (index != list.Count) throw new RpcException(RpcError.InvalidPayload, "Invalid Add index.");
                    goto case NetworkOperation.Insert;
                case NetworkOperation.Insert:
                    if (list.Count >= MaximumCount) throw new RpcException(RpcError.LimitExceeded, "List limit exceeded.");
                    list.Insert(index, value); break;
                case NetworkOperation.Set: list[index] = value; break;
                case NetworkOperation.Remove: list.RemoveAt(index); break;
                case NetworkOperation.Clear: list.Clear(); break;
                default: throw new RpcException(RpcError.InvalidPayload, "Invalid list operation.");
            }
        }
        private static void ValidateOperation(List<T> list, NetworkOperation op, int index)
        {
            switch (op)
            {
                case NetworkOperation.Add:
                    if (index != list.Count) throw new RpcException(RpcError.InvalidPayload, "Invalid Add index.");
                    goto case NetworkOperation.Insert;
                case NetworkOperation.Insert:
                    if (list.Count >= MaximumCount) throw new RpcException(RpcError.LimitExceeded, "List limit exceeded.");
                    if (index < 0 || index > list.Count) throw new RpcException(RpcError.InvalidPayload, "Invalid insertion index.");
                    break;
                case NetworkOperation.Set: case NetworkOperation.Remove:
                    if (index < 0 || index >= list.Count) throw new RpcException(RpcError.InvalidPayload, "Invalid list index.");
                    break;
                case NetworkOperation.Clear: break;
                default: throw new RpcException(RpcError.InvalidPayload, "Invalid list operation.");
            }
        }
        void INetworkState.Bind(bool authority, Action<INetworkState, NetMessageBag> publish) { lock (Gate) { Authority = authority; Publish = publish; } }
        NetMessageBag INetworkState.Snapshot()
        {
            lock (Gate) { var bag = NetMessageBag.Pool(); try { bag.Write(Revision); bag.Write(_items.Count); foreach (var item in _items) bag.Write(item); return bag; } catch { bag.Dispose(); throw; } }
        }
        bool INetworkState.Apply(NetMessageReader reader, bool snapshot, long revision)
        {
            NetworkChange change;
            lock (Gate)
            {
                if (revision < Revision || revision == Revision && Initialized) return true;
                if (!snapshot && revision != Revision + 1) return false;
                if (snapshot)
                {
                    var count = reader.Read<int>(); if (count < 0 || count > MaximumCount) throw new RpcException(RpcError.LimitExceeded, "Invalid list count.");
                    var next = _spare ?? new List<T>(count); _spare = null; next.Clear();
                    try
                    {
                        for (int i = 0; i < count; i++) next.Add(reader.Read<T>());
                        reader.Complete(); var previous = _items; _items = next; previous.Clear(); _spare = previous;
                    }
                    catch { next.Clear(); _spare = next; throw; }
                    change = new NetworkChange(NetworkOperation.Snapshot, revision);
                }
                else
                {
                    var op = (NetworkOperation)reader.Read<byte>(); var index = reader.Read<int>();
                    var value = op == NetworkOperation.Add || op == NetworkOperation.Insert || op == NetworkOperation.Set ? reader.Read<T>() : default!;
                    // Decode and preflight the entire single operation before changing live storage.
                    reader.Complete(); ValidateOperation(_items, op, index);
                    ApplyOperation(_items, op, index, value); change = new NetworkChange(op, revision, index);
                }
                Revision = revision; Initialized = true;
            }
            Notify(change); return true;
        }
    }
    public sealed class NetworkDictionary<TKey, TValue> : NetworkCollection, IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, INetworkState where TKey : notnull
    {
        public NetworkDictionary() { if (!NetValue<TKey>.Independent) throw new NotSupportedException("Network dictionary keys must be independent scalar/value types."); }
        private Dictionary<TKey, TValue> _items = new();
        private Dictionary<TKey, TValue>? _spare;
        ulong INetworkState.Schema => StateSchema.For(typeof(IDictionary<TKey, TValue>));
        public int Count { get { lock (Gate) return _items.Count; } }
        public bool IsReadOnly => !Authority;
        public ICollection<TKey> Keys { get { lock (Gate) return _items.Keys.ToArray(); } }
        public ICollection<TValue> Values { get { lock (Gate) return _items.Values.Select(NetValue<TValue>.Copy).ToArray(); } }
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
        public TValue this[TKey key] { get { lock (Gate) return NetValue<TValue>.Copy(_items[key]); } set => Change(NetworkOperation.Set, key, value); }
        public void Add(TKey key, TValue value) => Change(NetworkOperation.Add, key, value);
        public bool Remove(TKey key) => Change(NetworkOperation.Remove, key, default!);
        public void Clear() => Change(NetworkOperation.Clear, default!, default!);
        public bool ContainsKey(TKey key) { lock (Gate) return _items.ContainsKey(key); }
        public bool TryGetValue(TKey key, out TValue value) { lock (Gate) { bool found = _items.TryGetValue(key, out var item); value = found ? NetValue<TValue>.Copy(item!) : default!; return found; } }
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<TKey, TValue> item) => TryGetValue(item.Key, out var value) && NetValue<TValue>.Equal(value, item.Value);
        public bool Remove(KeyValuePair<TKey, TValue> item) => Change(NetworkOperation.Remove, item.Key, item.Value, true);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            lock (Gate) { if (arrayIndex < 0 || arrayIndex > array.Length || _items.Count > array.Length - arrayIndex) throw new ArgumentOutOfRangeException(nameof(arrayIndex)); foreach (var item in _items) array[arrayIndex++] = new KeyValuePair<TKey, TValue>(item.Key, NetValue<TValue>.Copy(item.Value)); }
        }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() { lock (Gate) return _items.Select(p => new KeyValuePair<TKey, TValue>(p.Key, NetValue<TValue>.Copy(p.Value))).ToList().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private bool Change(NetworkOperation op, TKey key, TValue value, bool requireValue = false)
        {
            NetMessageBag delta; NetworkChange change;
            lock (Gate)
            {
                CheckWrite();
                if (requireValue && (!_items.TryGetValue(key, out var expected) || !NetValue<TValue>.Equal(expected, value))) return false;
                if (op == NetworkOperation.Clear && _items.Count == 0 || op == NetworkOperation.Remove && !_items.ContainsKey(key) ||
                    op == NetworkOperation.Set && _items.TryGetValue(key, out var old) && NetValue<TValue>.Equal(old, value)) return false;
                value = NetValue<TValue>.Copy(value);
                if (Revision == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Revision exhausted.");
                delta = NetMessageBag.Pool();
                try
                {
                    delta.Write(Revision + 1); delta.Write((byte)op); if (op != NetworkOperation.Clear) delta.Write(key);
                    if (op == NetworkOperation.Add || op == NetworkOperation.Set) delta.Write(value);
                    ApplyOperation(_items, op, key, value); change = new NetworkChange(op, ++Revision);
                }
                catch { delta.Dispose(); throw; }
            }
            try { Publish?.Invoke(this, delta); } finally { delta.Dispose(); }
            Notify(change); return true;
        }
        private static void ApplyOperation(Dictionary<TKey, TValue> items, NetworkOperation op, TKey key, TValue value)
        {
            if (op != NetworkOperation.Clear && key == null) throw new RpcException(RpcError.InvalidPayload, "Null dictionary key.");
            if ((op == NetworkOperation.Add || op == NetworkOperation.Set) && !items.ContainsKey(key) && items.Count == MaximumCount)
                throw new RpcException(RpcError.LimitExceeded, "Dictionary capacity exceeded.");
            switch (op)
            {
                case NetworkOperation.Add: items.Add(key, value); break;
                case NetworkOperation.Set: items[key] = value; break;
                case NetworkOperation.Remove: if (!items.Remove(key)) throw new RpcException(RpcError.InvalidPayload, "Missing dictionary key."); break;
                case NetworkOperation.Clear: items.Clear(); break;
                default: throw new RpcException(RpcError.InvalidPayload, "Invalid dictionary operation.");
            }
        }
        private static void ValidateOperation(Dictionary<TKey, TValue> items, NetworkOperation op, TKey key)
        {
            if (op == NetworkOperation.Clear) return;
            if (key == null) throw new RpcException(RpcError.InvalidPayload, "Null dictionary key.");
            bool present = items.ContainsKey(key);
            if (op == NetworkOperation.Add && present) throw new RpcException(RpcError.InvalidPayload, "Duplicate dictionary key.");
            if (op == NetworkOperation.Remove && !present) throw new RpcException(RpcError.InvalidPayload, "Missing dictionary key.");
            if (op != NetworkOperation.Add && op != NetworkOperation.Set && op != NetworkOperation.Remove) throw new RpcException(RpcError.InvalidPayload, "Invalid dictionary operation.");
            if ((op == NetworkOperation.Add || op == NetworkOperation.Set) && !present && items.Count == MaximumCount) throw new RpcException(RpcError.LimitExceeded, "Dictionary capacity exceeded.");
        }
        void INetworkState.Bind(bool authority, Action<INetworkState, NetMessageBag> publish) { lock (Gate) { Authority = authority; Publish = publish; } }
        NetMessageBag INetworkState.Snapshot()
        {
            lock (Gate) { var bag = NetMessageBag.Pool(); try { bag.Write(Revision); bag.Write(_items.Count); foreach (var item in _items) { bag.Write(item.Key); bag.Write(item.Value); } return bag; } catch { bag.Dispose(); throw; } }
        }
        bool INetworkState.Apply(NetMessageReader reader, bool snapshot, long revision)
        {
            NetworkChange change;
            lock (Gate)
            {
                if (revision < Revision || revision == Revision && Initialized) return true;
                if (!snapshot && revision != Revision + 1) return false;
                if (snapshot)
                {
                    var count = reader.Read<int>(); if (count < 0 || count > MaximumCount) throw new RpcException(RpcError.LimitExceeded, "Invalid dictionary count.");
                    var next = _spare ?? new Dictionary<TKey, TValue>(count); _spare = null; next.Clear();
                    try
                    {
                        for (int i = 0; i < count; i++) next.Add(reader.Read<TKey>(), reader.Read<TValue>());
                        reader.Complete(); var previous = _items; _items = next; previous.Clear(); _spare = previous;
                    }
                    catch { next.Clear(); _spare = next; throw; }
                    change = new NetworkChange(NetworkOperation.Snapshot, revision);
                }
                else
                {
                    var op = (NetworkOperation)reader.Read<byte>(); var key = op == NetworkOperation.Clear ? default! : reader.Read<TKey>();
                    var value = op == NetworkOperation.Add || op == NetworkOperation.Set ? reader.Read<TValue>() : default!;
                    reader.Complete(); ValidateOperation(_items, op, key);
                    ApplyOperation(_items, op, key, value); change = new NetworkChange(op, revision);
                }
                Revision = revision; Initialized = true;
            }
            Notify(change); return true;
        }
    }
    internal static class StateSchema
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ulong> Cache = new();
        public static ulong For(Type type) => Cache.GetOrAdd(type, t => Hash(System.Text.Encoding.UTF8.GetBytes(Describe(t, new HashSet<Type>()))));
        private static string Describe(Type type, HashSet<Type> visited)
        {
            if (type.IsArray) return "array:" + Describe(type.GetElementType()!, visited);
            if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(t => Describe(t, visited))) + ">";
            if (type.IsEnum) return type.FullName + ":" + string.Join(",", Enum.GetNames(type).Select(n => n + "=" + Convert.ToString(Convert.ChangeType(Enum.Parse(type, n), Enum.GetUnderlyingType(type)), System.Globalization.CultureInfo.InvariantCulture)));
            if (type.IsPrimitive || type.Namespace == "System" || !visited.Add(type)) return type.FullName ?? type.Name;
            try
            {
                return type.FullName + "{" + string.Join(",", type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Select(f => f.Name + ":" + Describe(f.FieldType, visited))
                    .Concat(type.GetProperties().Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name + ":" + Describe(p.PropertyType, visited))).OrderBy(n => n, StringComparer.Ordinal)) + "}";
            }
            finally { visited.Remove(type); }
        }
        public static ulong Hash(ReadOnlySpan<byte> bytes)
        { unchecked { ulong hash = 14695981039346656037UL; foreach (var b in bytes) { hash ^= b; hash *= 1099511628211UL; } return hash; } }
    }
}
