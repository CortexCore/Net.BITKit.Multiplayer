using System;
using System.Collections;
using System.Collections.Generic;

namespace BITKit.Multiplayer
{
public sealed class SyncHashSet<T> : SyncCollection<SyncHashSetChange<T>>, IReadOnlyCollection<T> where T : notnull
{
    private HashSet<T> _items = new HashSet<T>();
    private HashSet<T>? _spare;
    public SyncHashSet() { SyncValue<T>.Check(true); }
    public int Count { get { lock (Gate) return _items.Count; } }
    public bool Contains(T value) { lock (Gate) return _items.Contains(value); }
    public bool Add(T value) => Change(new SyncHashSetChange<T>(SyncOperation.Add, value));
    public bool Remove(T value) => Change(new SyncHashSetChange<T>(SyncOperation.Remove, value));
    public void Clear() => Change(new SyncHashSetChange<T>(SyncOperation.Clear));
    public readonly struct Edit
    {
        internal readonly SyncHashSetChange<T> Change;
        private Edit(SyncOperation operation, T value = default!) => Change = new SyncHashSetChange<T>(operation, value);
        public static Edit Add(T value) => new Edit(SyncOperation.Add, value);
        public static Edit Remove(T value) => new Edit(SyncOperation.Remove, value);
        public static Edit Clear() => new Edit(SyncOperation.Clear);
    }
    public void ApplyBatch(params Edit[] edits) => ChangeMany(Bounded(Commands(edits)));
    private static IEnumerable<SyncHashSetChange<T>> Commands(IEnumerable<Edit> edits)
    { if (edits == null) throw new ArgumentNullException(nameof(edits)); foreach (var edit in edits) yield return edit.Change; }
    public void UnionWith(IEnumerable<T> values) => ChangeMany(Bounded(Edits(values)));
    private static IEnumerable<SyncHashSetChange<T>> Edits(IEnumerable<T> values)
    { if (values == null) throw new ArgumentNullException(nameof(values)); foreach (var value in values) yield return new SyncHashSetChange<T>(SyncOperation.Add, value); }
    public HashSet<T>.Enumerator GetEnumerator() { lock (Gate) return _items.GetEnumerator(); }
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal override byte Kind => 3;
    internal override object Store { get => _items; set => _items = (HashSet<T>)value; }
    internal override object CopyStore(bool empty)
    {
        var next = _spare ?? new HashSet<T>(); _spare = null;
        next.Clear(); if (!empty) foreach (var value in _items) next.Add(value); return next;
    }
    internal override void RecycleStore(object store)
    { var old = (HashSet<T>)store; old.Clear(); _spare = old; }
    internal override SyncOperation Operation(SyncHashSetChange<T> change) => change.Operation;
    internal override SyncHashSetChange<T> Prepare(SyncHashSetChange<T> c, bool remote)
    {
        if (c.Operation == SyncOperation.Clear) return _items.Count == 0 ? default : c;
        if (c.Value == null) throw new RpcException(RpcError.InvalidPayload, "Null set element");
        if (c.Operation == SyncOperation.Add)
        {
            if (_items.Contains(c.Value)) return default;
            if (_items.Count == MaximumCount) throw new RpcException(RpcError.LimitExceeded, "SyncHashSet capacity exceeded");
            return c;
        }
        if (c.Operation == SyncOperation.Remove) return _items.Contains(c.Value) ? c : default;
        throw new RpcException(RpcError.InvalidPayload, "Invalid set operation");
    }
    internal override void ApplyPrepared(SyncHashSetChange<T> c)
    {
        if (c.Operation == SyncOperation.Clear) _items.Clear();
        else if (c.Operation == SyncOperation.Remove) _items.Remove(c.Value);
        else _items.Add(c.Value);
    }
    internal override int SizeChange(SyncHashSetChange<T> c) => c.Operation switch
    {
        SyncOperation.Add => SyncValue<T>.Size(c.Value),
        SyncOperation.Remove => -SyncValue<T>.Size(c.Value),
        SyncOperation.Clear => 4 - EncodedSize,
        _ => 0
    };
    internal override void WriteChange(BinaryBufferWriter w, SyncHashSetChange<T> c)
    { SyncWire.Byte(w, (byte)c.Operation); if (c.Operation != SyncOperation.Clear) SyncValue<T>.Write(w, c.Value); }
    internal override SyncHashSetChange<T> ReadChange(ref ReadOnlySpan<byte> input)
    {
        var op = (SyncOperation)SyncWire.Take(ref input, 1)[0];
        if (op == SyncOperation.Clear) return new SyncHashSetChange<T>(op);
        if (op != SyncOperation.Add && op != SyncOperation.Remove) throw new RpcException(RpcError.InvalidPayload, "Invalid set opcode");
        return new SyncHashSetChange<T>(op, SyncValue<T>.Read(ref input));
    }
    internal override byte[] SnapshotBody()
    {
        using var w = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        SyncWire.Int(w, _items.Count); foreach (var value in _items) SyncValue<T>.Write(w, value); return w.CopyOwned();
    }
    internal override int ReadSnapshot(ref ReadOnlySpan<byte> input)
    {
        int size = input.Length, count = SyncWire.Count(ref input, MaximumCount);
        for (int i = 0; i < count; i++)
        {
            var value = SyncValue<T>.Read(ref input);
            if (value == null || !_items.Add(value)) throw new RpcException(RpcError.InvalidPayload, "Duplicate/null set snapshot element");
        }
        return size - input.Length;
    }
    internal override SyncHashSetChange<T> Stamp(SyncHashSetChange<T> c, long v, SyncChangeOrigin origin) => new SyncHashSetChange<T>(c.Operation, c.Value, v, origin);
    internal override SyncHashSetChange<T> Reset(long v) => new SyncHashSetChange<T>(SyncOperation.Reset, version: v, origin: SyncChangeOrigin.Snapshot);
}
}
