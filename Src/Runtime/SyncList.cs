using System;
using System.Collections;
using System.Collections.Generic;

namespace BITKit.Multiplayer
{
public sealed class SyncList<T> : SyncCollection<SyncListChange<T>>, IReadOnlyList<T>
{
    private List<T> _items;
    private List<T>? _spare;
    public SyncList(int capacity = 0)
    {
        if (capacity < 0 || capacity > MaximumCount) throw new ArgumentOutOfRangeException(nameof(capacity));
        SyncValue<T>.Check(); _items = new List<T>(capacity);
    }
    public int Count { get { lock (Gate) return _items.Count; } }
    public T this[int index] { get { lock (Gate) return SyncValue<T>.Copy(_items[index]); } set => Change(new SyncListChange<T>(SyncOperation.Set, index, newValue: value)); }
    public void Add(T value) => Change(new SyncListChange<T>(SyncOperation.Add, newValue: value));
    public void Insert(int index, T value) => Change(new SyncListChange<T>(SyncOperation.Insert, index, newValue: value));
    public bool Remove(T value) => Change(new SyncListChange<T>(SyncOperation.Remove, -2, oldValue: value));
    public void RemoveAt(int index) => Change(new SyncListChange<T>(SyncOperation.Remove, index));
    public void Clear() => Change(new SyncListChange<T>(SyncOperation.Clear));
    public readonly struct Edit
    {
        internal readonly SyncListChange<T> Change;
        private Edit(SyncOperation operation, int index = -1, T value = default!) => Change = new SyncListChange<T>(operation, index, newValue: value);
        public static Edit Add(T value) => new Edit(SyncOperation.Add, value: value);
        public static Edit Insert(int index, T value) => new Edit(SyncOperation.Insert, index, value);
        public static Edit Set(int index, T value) => new Edit(SyncOperation.Set, index, value);
        public static Edit RemoveAt(int index) => new Edit(SyncOperation.Remove, index);
        public static Edit Clear() => new Edit(SyncOperation.Clear);
    }
    public void ApplyBatch(params Edit[] edits) => ChangeMany(Bounded(Commands(edits)));
    private static IEnumerable<SyncListChange<T>> Commands(IEnumerable<Edit> edits)
    { if (edits == null) throw new ArgumentNullException(nameof(edits)); foreach (var edit in edits) yield return edit.Change; }
    public int IndexOf(T value) { lock (Gate) { for (int i = 0; i < _items.Count; i++) if (SyncValue<T>.Equal(_items[i], value)) return i; return -1; } }
    public bool Contains(T value) => IndexOf(value) >= 0;
    public void AddRange(IEnumerable<T> values) => ChangeMany(Bounded(Edits(values)));
    private static IEnumerable<SyncListChange<T>> Edits(IEnumerable<T> values)
    { if (values == null) throw new ArgumentNullException(nameof(values)); foreach (var value in values) yield return new SyncListChange<T>(SyncOperation.Add, newValue: value); }
    public Enumerator GetEnumerator() { lock (Gate) return new Enumerator(_items.GetEnumerator()); }
    public struct Enumerator : IEnumerator<T>
    {
        private List<T>.Enumerator _inner;
        internal Enumerator(List<T>.Enumerator inner) => _inner = inner;
        public T Current => SyncValue<T>.Copy(_inner.Current);
        object? IEnumerator.Current => Current;
        public bool MoveNext() => _inner.MoveNext();
        public void Dispose() => _inner.Dispose();
        public void Reset() => throw new NotSupportedException();
    }
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal override byte Kind => 1;
    internal override object Store { get => _items; set => _items = (List<T>)value; }
    internal override object CopyStore(bool empty)
    {
        var next = _spare ?? new List<T>(_items.Count); _spare = null;
        next.Clear(); if (!empty) next.AddRange(_items); return next;
    }
    internal override void RecycleStore(object store)
    { var old = (List<T>)store; old.Clear(); _spare = old; }
    internal override SyncOperation Operation(SyncListChange<T> change) => change.Operation;
    internal override SyncListChange<T> Prepare(SyncListChange<T> c, bool remote)
    {
        int index = c.Index;
        if (c.Operation == SyncOperation.Clear) return _items.Count == 0 ? default : c;
        if (c.Operation == SyncOperation.Add || c.Operation == SyncOperation.Insert)
        {
            if (_items.Count == MaximumCount) throw new RpcException(RpcError.LimitExceeded, "SyncList capacity exceeded");
            if (c.Operation == SyncOperation.Add && !remote) index = _items.Count;
            if (index < 0 || index > _items.Count || remote && c.Operation == SyncOperation.Add && index != _items.Count)
                throw new RpcException(RpcError.InvalidPayload, "Invalid list insertion index");
            return new SyncListChange<T>(c.Operation, index, newValue: remote ? c.NewValue : SyncValue<T>.Copy(c.NewValue));
        }
        if (c.Operation == SyncOperation.Remove && index == -2 && !remote)
        { index = IndexOf(c.OldValue); if (index < 0) return default; }
        if (index < 0 || index >= _items.Count) throw new RpcException(RpcError.InvalidPayload, "Invalid list index");
        if (c.Operation == SyncOperation.Set)
            return SyncValue<T>.Equal(_items[index], c.NewValue) ? default : new SyncListChange<T>(c.Operation, index, _items[index], remote ? c.NewValue : SyncValue<T>.Copy(c.NewValue));
        if (c.Operation == SyncOperation.Remove) return new SyncListChange<T>(c.Operation, index, _items[index]);
        throw new RpcException(RpcError.InvalidPayload, "Invalid list operation");
    }
    internal override void ApplyPrepared(SyncListChange<T> c)
    {
        switch (c.Operation)
        {
            case SyncOperation.Add: case SyncOperation.Insert: _items.Insert(c.Index, c.NewValue); break;
            case SyncOperation.Set: _items[c.Index] = c.NewValue; break;
            case SyncOperation.Remove: _items.RemoveAt(c.Index); break;
            case SyncOperation.Clear: _items.Clear(); break;
        }
    }
    internal override int SizeChange(SyncListChange<T> c) => c.Operation switch
    {
        SyncOperation.Add or SyncOperation.Insert => SyncValue<T>.Size(c.NewValue),
        SyncOperation.Set => SyncValue<T>.Size(c.NewValue) - SyncValue<T>.Size(c.OldValue),
        SyncOperation.Remove => -SyncValue<T>.Size(c.OldValue),
        SyncOperation.Clear => 4 - EncodedSize,
        _ => 0
    };
    internal override void WriteChange(BinaryBufferWriter w, SyncListChange<T> c)
    {
        SyncWire.Byte(w, (byte)c.Operation);
        if (c.Operation == SyncOperation.Clear) return;
        SyncWire.Int(w, c.Index);
        if (c.Operation != SyncOperation.Remove) SyncValue<T>.Write(w, c.NewValue);
    }
    internal override SyncListChange<T> ReadChange(ref ReadOnlySpan<byte> input)
    {
        var op = (SyncOperation)SyncWire.Take(ref input, 1)[0];
        if (op == SyncOperation.Clear) return new SyncListChange<T>(op);
        if (op != SyncOperation.Add && op != SyncOperation.Insert && op != SyncOperation.Set && op != SyncOperation.Remove)
            throw new RpcException(RpcError.InvalidPayload, "Invalid list opcode");
        int index = SyncWire.ReadInt(ref input);
        return new SyncListChange<T>(op, index, newValue: op == SyncOperation.Remove ? default! : SyncValue<T>.Read(ref input));
    }
    internal override byte[] SnapshotBody()
    {
        using var w = BinaryBufferWriter.Rent(SyncWire.Maximum - SyncWire.Header);
        SyncWire.Int(w, _items.Count); foreach (var item in _items) SyncValue<T>.Write(w, item); return w.CopyOwned();
    }
    internal override int ReadSnapshot(ref ReadOnlySpan<byte> input)
    {
        int size = input.Length, count = SyncWire.Count(ref input, MaximumCount);
        for (int i = 0; i < count; i++) _items.Add(SyncValue<T>.Read(ref input));
        return size - input.Length;
    }
    internal override SyncListChange<T> Stamp(SyncListChange<T> c, long v, SyncChangeOrigin origin) => new SyncListChange<T>(c.Operation, c.Index, SyncValue<T>.Copy(c.OldValue), SyncValue<T>.Copy(c.NewValue), v, origin);
    internal override SyncListChange<T> Reset(long v) => new SyncListChange<T>(SyncOperation.Reset, version: v, origin: SyncChangeOrigin.Snapshot);
}
}
