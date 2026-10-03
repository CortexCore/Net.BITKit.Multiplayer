using System;
using System.Collections;
using System.Collections.Generic;

namespace BITKit.Multiplayer
{
public sealed class SyncDictionary<TKey, TValue> : SyncCollection<SyncDictionaryChange<TKey, TValue>>, IReadOnlyDictionary<TKey, TValue> where TKey : notnull
{
    private Dictionary<TKey, TValue> _items;
    private Dictionary<TKey, TValue>? _spare;
    public SyncDictionary(int capacity = 0)
    {
        if (capacity < 0 || capacity > MaximumCount) throw new ArgumentOutOfRangeException(nameof(capacity));
        SyncValue<TKey>.Check(true); SyncValue<TValue>.Check(); _items = new Dictionary<TKey, TValue>(capacity);
    }
    public int Count { get { lock (Gate) return _items.Count; } }
    public TValue this[TKey key] { get { lock (Gate) return SyncValue<TValue>.Copy(_items[key]); } set => Change(new SyncDictionaryChange<TKey, TValue>(SyncOperation.Set, key, newValue: value)); }
    public IEnumerable<TKey> Keys { get { foreach (var key in _items.Keys) yield return key; } }
    public IEnumerable<TValue> Values { get { foreach (var pair in this) yield return pair.Value; } }
    public bool ContainsKey(TKey key) { lock (Gate) return _items.ContainsKey(key); }
    public bool TryGetValue(TKey key, out TValue value)
    { lock (Gate) { bool found = _items.TryGetValue(key, out var stored); value = found ? SyncValue<TValue>.Copy(stored!) : default!; return found; } }
    public void Add(TKey key, TValue value) => Change(new SyncDictionaryChange<TKey, TValue>(SyncOperation.Add, key, newValue: value));
    public bool Remove(TKey key) => Change(new SyncDictionaryChange<TKey, TValue>(SyncOperation.Remove, key));
    public void Clear() => Change(new SyncDictionaryChange<TKey, TValue>(SyncOperation.Clear));
    public readonly struct Edit
    {
        internal readonly SyncDictionaryChange<TKey, TValue> Change;
        private Edit(SyncOperation operation, TKey key = default!, TValue value = default!) => Change = new SyncDictionaryChange<TKey, TValue>(operation, key, newValue: value);
        public static Edit Add(TKey key, TValue value) => new Edit(SyncOperation.Add, key, value);
        public static Edit Set(TKey key, TValue value) => new Edit(SyncOperation.Set, key, value);
        public static Edit Remove(TKey key) => new Edit(SyncOperation.Remove, key);
        public static Edit Clear() => new Edit(SyncOperation.Clear);
    }
    public void ApplyBatch(params Edit[] edits) => ChangeMany(Bounded(Commands(edits)));
    private static IEnumerable<SyncDictionaryChange<TKey, TValue>> Commands(IEnumerable<Edit> edits)
    { if (edits == null) throw new ArgumentNullException(nameof(edits)); foreach (var edit in edits) yield return edit.Change; }
    public void SetRange(IEnumerable<KeyValuePair<TKey, TValue>> values) => ChangeMany(Bounded(Edits(values)));
    private static IEnumerable<SyncDictionaryChange<TKey, TValue>> Edits(IEnumerable<KeyValuePair<TKey, TValue>> values)
    { if (values == null) throw new ArgumentNullException(nameof(values)); foreach (var value in values) yield return new SyncDictionaryChange<TKey, TValue>(SyncOperation.Set, value.Key, newValue: value.Value); }
    public Enumerator GetEnumerator() { lock (Gate) return new Enumerator(_items.GetEnumerator()); }
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private Dictionary<TKey, TValue>.Enumerator _inner;
        internal Enumerator(Dictionary<TKey, TValue>.Enumerator inner) => _inner = inner;
        public KeyValuePair<TKey, TValue> Current => new KeyValuePair<TKey, TValue>(_inner.Current.Key, SyncValue<TValue>.Copy(_inner.Current.Value));
        object IEnumerator.Current => Current;
        public bool MoveNext() => _inner.MoveNext();
        public void Dispose() => _inner.Dispose();
        public void Reset() => throw new NotSupportedException();
    }
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal override byte Kind => 2;
    internal override object Store { get => _items; set => _items = (Dictionary<TKey, TValue>)value; }
    internal override object CopyStore(bool empty)
    {
        var next = _spare ?? new Dictionary<TKey, TValue>(_items.Count); _spare = null;
        next.Clear(); if (!empty) foreach (var pair in _items) next.Add(pair.Key, pair.Value);
        return next;
    }
    internal override void RecycleStore(object store)
    { var old = (Dictionary<TKey, TValue>)store; old.Clear(); _spare = old; }
    internal override SyncOperation Operation(SyncDictionaryChange<TKey, TValue> change) => change.Operation;
    internal override SyncDictionaryChange<TKey, TValue> Prepare(SyncDictionaryChange<TKey, TValue> c, bool remote)
    {
        if (c.Operation == SyncOperation.Clear) return _items.Count == 0 ? default : c;
        if (c.Key == null) throw new RpcException(RpcError.InvalidPayload, "Null dictionary key");
        bool exists = _items.TryGetValue(c.Key, out var old);
        if (c.Operation == SyncOperation.Remove)
            return exists ? new SyncDictionaryChange<TKey, TValue>(c.Operation, c.Key, old!) : default;
        if (c.Operation != SyncOperation.Add && c.Operation != SyncOperation.Set)
            throw new RpcException(RpcError.InvalidPayload, "Invalid dictionary operation");
        if (c.Operation == SyncOperation.Add && exists || remote && c.Operation == SyncOperation.Set && !exists)
            throw new RpcException(RpcError.InvalidPayload, "Dictionary delta disagrees with key existence");
        if (exists && SyncValue<TValue>.Equal(old!, c.NewValue)) return default;
        if (!exists && _items.Count == MaximumCount) throw new RpcException(RpcError.LimitExceeded, "SyncDictionary capacity exceeded");
        return new SyncDictionaryChange<TKey, TValue>(exists ? SyncOperation.Set : SyncOperation.Add, c.Key, old!, remote ? c.NewValue : SyncValue<TValue>.Copy(c.NewValue));
    }
    internal override void ApplyPrepared(SyncDictionaryChange<TKey, TValue> c)
    {
        if (c.Operation == SyncOperation.Clear) _items.Clear();
        else if (c.Operation == SyncOperation.Remove) _items.Remove(c.Key);
        else _items[c.Key] = c.NewValue;
    }
    internal override int SizeChange(SyncDictionaryChange<TKey, TValue> c) => c.Operation switch
    {
        SyncOperation.Add => SyncValue<TKey>.Size(c.Key) + SyncValue<TValue>.Size(c.NewValue),
        SyncOperation.Set => SyncValue<TValue>.Size(c.NewValue) - SyncValue<TValue>.Size(c.OldValue),
        SyncOperation.Remove => -SyncValue<TKey>.Size(c.Key) - SyncValue<TValue>.Size(c.OldValue),
        SyncOperation.Clear => 4 - EncodedSize,
        _ => 0
    };
    internal override void WriteChange(BinaryBufferWriter w, SyncDictionaryChange<TKey, TValue> c)
    {
        SyncWire.Byte(w, (byte)c.Operation); if (c.Operation == SyncOperation.Clear) return;
        SyncValue<TKey>.Write(w, c.Key); if (c.Operation != SyncOperation.Remove) SyncValue<TValue>.Write(w, c.NewValue);
    }
    internal override SyncDictionaryChange<TKey, TValue> ReadChange(ref ReadOnlySpan<byte> input)
    {
        var op = (SyncOperation)SyncWire.Take(ref input, 1)[0];
        if (op == SyncOperation.Clear) return new SyncDictionaryChange<TKey, TValue>(op);
        if (op != SyncOperation.Add && op != SyncOperation.Set && op != SyncOperation.Remove)
            throw new RpcException(RpcError.InvalidPayload, "Invalid dictionary opcode");
        var key = SyncValue<TKey>.Read(ref input);
        return new SyncDictionaryChange<TKey, TValue>(op, key, newValue: op == SyncOperation.Remove ? default! : SyncValue<TValue>.Read(ref input));
    }
    internal override byte[] SnapshotBody()
    {
        using var w = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        SyncWire.Int(w, _items.Count);
        foreach (var pair in _items) { SyncValue<TKey>.Write(w, pair.Key); SyncValue<TValue>.Write(w, pair.Value); }
        return w.CopyOwned();
    }
    internal override int ReadSnapshot(ref ReadOnlySpan<byte> input)
    {
        int size = input.Length, count = SyncWire.Count(ref input, MaximumCount);
        for (int i = 0; i < count; i++)
        {
            var key = SyncValue<TKey>.Read(ref input); var value = SyncValue<TValue>.Read(ref input);
            if (key == null || !_items.TryAdd(key, value)) throw new RpcException(RpcError.InvalidPayload, "Duplicate/null dictionary snapshot key");
        }
        return size - input.Length;
    }
    internal override SyncDictionaryChange<TKey, TValue> Stamp(SyncDictionaryChange<TKey, TValue> c, long v, SyncChangeOrigin origin) => new SyncDictionaryChange<TKey, TValue>(c.Operation, c.Key, SyncValue<TValue>.Copy(c.OldValue), SyncValue<TValue>.Copy(c.NewValue), v, origin);
    internal override SyncDictionaryChange<TKey, TValue> Reset(long v) => new SyncDictionaryChange<TKey, TValue>(SyncOperation.Reset, version: v, origin: SyncChangeOrigin.Snapshot);
}
}
