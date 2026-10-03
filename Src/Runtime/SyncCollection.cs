using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace BITKit.Multiplayer
{
public abstract class SyncCollection
{
    internal readonly object Gate = new object();
    internal CollectionBinding? Binding;
    internal bool EverBound;
    internal long Revision;
    internal int EncodedSize = 4;
    internal SyncCollection() { }
    public long Version { get { lock (Gate) return Revision; } }
    public const int MaximumCount = SyncWire.MaxItems;
    internal abstract byte Kind { get; }
    internal abstract byte[] SnapshotBody();
    internal abstract void ApplyFrame(byte[] frame, ulong fingerprint, long revision, bool delta);
    internal abstract void ConnectHook(object owner, MethodInfo? hook);
    internal abstract void Notify();
    internal abstract void ClearNotifications();
    internal byte[] Snapshot(ulong fingerprint) => SyncWire.Wrap(Kind, fingerprint, -1, SnapshotBody());
}
internal sealed class CollectionBinding
{
    internal RpcRuntime Runtime = null!;
    internal TargetKey Key;
    internal object Token = null!;
    internal string Property = "";
    internal ulong Fingerprint;
    internal volatile bool Active;
}

public abstract class SyncCollection<TChange> : SyncCollection where TChange : struct
{
    private readonly Queue<TChange> _notifications = new Queue<TChange>();
    private SyncChanged<TChange>? _hook;
    private int _notifying, _notificationThread;
    private SyncChanged<TChange>? _changed;
    private SyncChanged<TChange>[] _listeners = Array.Empty<SyncChanged<TChange>>();
    public event SyncChanged<TChange>? Changed
    {
        add { lock (Gate) { _changed += value; RefreshListeners(); } }
        remove { lock (Gate) { _changed -= value; RefreshListeners(); } }
    }
    private void RefreshListeners()
    {
        var all = _changed?.GetInvocationList();
        var listeners = all == null ? Array.Empty<SyncChanged<TChange>>() : new SyncChanged<TChange>[all.Length];
        for (int i = 0; i < listeners.Length; i++) listeners[i] = (SyncChanged<TChange>)all![i];
        Volatile.Write(ref _listeners, listeners);
    }
    internal SyncCollection() { }
    internal abstract SyncOperation Operation(TChange change);
    internal abstract TChange Prepare(TChange requested, bool remote);
    internal abstract void ApplyPrepared(TChange change);
    internal abstract int SizeChange(TChange change);
    internal abstract void WriteChange(BinaryBufferWriter writer, TChange change);
    internal abstract TChange ReadChange(ref ReadOnlySpan<byte> input);
    internal abstract TChange Stamp(TChange change, long version, SyncChangeOrigin origin);
    internal abstract TChange Reset(long version);
    internal abstract object Store { get; set; }
    internal abstract object CopyStore(bool empty);
    internal abstract void RecycleStore(object store);
    internal abstract int ReadSnapshot(ref ReadOnlySpan<byte> input);

    protected bool Change(TChange change) => ChangeCore(change, null);
    protected bool ChangeMany(TChange[] changes) => ChangeCore(default, changes);
    private bool ChangeCore(TChange change, TChange[]? changes)
    {
        while (true)
        {
            var binding = Volatile.Read(ref Binding);
            if (binding != null) return binding.Runtime.ChangeCollection(binding, this, change, changes);
            lock (Gate)
            {
                if (Binding != null) continue;
                if (EverBound) throw new RpcException(RpcError.Disposed, "Detached synchronized collection");
                return Commit(change, changes, false, out _) != null;
            }
        }
    }
    // Called with runtime gate -> collection Gate. All potentially failing encoding
    // precedes a single mutation; multi-operation batches use a private staging store.
    internal byte[]? Commit(TChange single, TChange[]? batch, bool notify, out long basis)
    {
        notify &= _hook != null || _listeners.Length != 0;
        basis = Revision;
        if (_notificationThread == Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A collection callback cannot mutate the same collection reentrantly");
        int count = batch?.Length ?? 1;
        if (count > SyncWire.MaxOperations) throw new RpcException(RpcError.LimitExceeded, "Collection batch exceeds 64 operations");
        if (Revision == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Collection revision exhausted");
        if (_notifications.Count + count > SyncWire.MaxItems)
            throw new RpcException(RpcError.LimitExceeded, "Collection notification backlog exceeded");
        // Nothing is rented/allocated for a no-op scalar edit.
        if (batch == null)
        {
            var actual = Prepare(single, false);
            if (Operation(actual) == SyncOperation.None) return null;
            int size = checked(EncodedSize + SizeChange(actual));
            if (size > SyncWire.Maximum - SyncWire.Header)
                throw new RpcException(RpcError.LimitExceeded, "Collection snapshot exceeds 16 KiB");
            using var singleWriter = BinaryBufferWriter.Rent(SyncWire.Maximum);
            SyncWire.HeaderTo(singleWriter, Kind, Binding?.Fingerprint ?? 0, basis);
            SyncWire.Int(singleWriter, 1); WriteChange(singleWriter, actual);
            var notice = notify ? Stamp(actual, Revision + 1, SyncChangeOrigin.Local) : default;
            var frame = singleWriter.CopyOwned(); // the one independently owned queued payload
            ApplyPrepared(actual); EncodedSize = size; Revision++;
            if (notify) _notifications.Enqueue(notice);
            return frame;
        }
        using var writer = BinaryBufferWriter.Rent(SyncWire.Maximum);
        SyncWire.HeaderTo(writer, Kind, Binding?.Fingerprint ?? 0, basis);
        SyncWire.Int(writer, 0);
        var original = Store; int originalSize = EncodedSize;
        var actualBatch = new List<TChange>(count);
        Store = CopyStore(false);
        int changed = 0, finalSize = EncodedSize;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var actual = Prepare(batch[i], false);
                if (Operation(actual) == SyncOperation.None) continue;
                finalSize = checked(EncodedSize + SizeChange(actual));
                if (finalSize > SyncWire.Maximum - SyncWire.Header)
                    throw new RpcException(RpcError.LimitExceeded, "Collection snapshot exceeds 16 KiB");
                WriteChange(writer, actual); changed++;
                ApplyPrepared(actual); EncodedSize = finalSize; actualBatch.Add(actual);
            }
            if (changed == 0) { var unused = Store; Store = original; RecycleStore(unused); return null; }
            writer.PatchInt32(SyncWire.Header, changed);
            var result = writer.CopyOwned();
            if (notify)
                for (int i = 0; i < actualBatch.Count; i++) actualBatch[i] = Stamp(actualBatch[i], Revision + 1, SyncChangeOrigin.Local);
            Revision++;
            if (notify) foreach (var actual in actualBatch) _notifications.Enqueue(actual);
            RecycleStore(original);
            return result;
        }
        catch { var abandoned = Store; Store = original; EncodedSize = originalSize; RecycleStore(abandoned); throw; }
    }
    internal override void ApplyFrame(byte[] frame, ulong fingerprint, long revision, bool delta)
    {
        bool notify = _hook != null || _listeners.Length != 0;
        var input = SyncWire.Open(frame, Kind, fingerprint, out long basis);
        if (delta && basis != Revision || !delta && basis != -1)
            throw new RpcException(RpcError.InvalidPayload, "Collection base revision mismatch");
        if (_notifications.Count + (delta ? SyncWire.MaxOperations : 1) > SyncWire.MaxItems)
            throw new RpcException(RpcError.LimitExceeded, "Collection notification backlog exceeded");
        int count = delta ? SyncWire.Count(ref input, SyncWire.MaxOperations) : 0;
        if (delta && (count == 0 || basis == long.MaxValue || revision != basis + 1))
            throw new RpcException(RpcError.InvalidPayload, "Invalid collection revision/batch");
        // A single operation can be completely decoded, validated and its notification
        // prepared before mutation. It needs no O(collection size) staging clone.
        if (delta && count == 1)
        {
            var actual = Prepare(ReadChange(ref input), true);
            if (Operation(actual) == SyncOperation.None) throw new RpcException(RpcError.InvalidPayload, "Invalid no-op collection delta");
            SyncWire.End(input);
            int size = checked(EncodedSize + SizeChange(actual));
            if (size > SyncWire.Maximum - SyncWire.Header) throw new RpcException(RpcError.LimitExceeded, "Collection snapshot exceeds 16 KiB");
            var notification = notify ? Stamp(actual, revision, SyncChangeOrigin.Remote) : default;
            ApplyPrepared(actual); EncodedSize = size; Revision = revision;
            if (notify) _notifications.Enqueue(notification);
            return;
        }
        var original = Store; int originalSize = EncodedSize;
        Store = CopyStore(!delta);
        var changes = notify ? new List<TChange>(delta ? count : 1) : null;
        try
        {
            if (!delta) { EncodedSize = ReadSnapshot(ref input); if (notify) changes!.Add(Reset(revision)); }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var actual = Prepare(ReadChange(ref input), true);
                    if (Operation(actual) == SyncOperation.None) throw new RpcException(RpcError.InvalidPayload, "Invalid no-op collection delta");
                    EncodedSize = checked(EncodedSize + SizeChange(actual)); ApplyPrepared(actual);
                    if (notify) changes!.Add(Stamp(actual, revision, SyncChangeOrigin.Remote));
                }
            }
            SyncWire.End(input);
            if (EncodedSize > SyncWire.Maximum - SyncWire.Header) throw new RpcException(RpcError.LimitExceeded, "Collection snapshot exceeds 16 KiB");
            Revision = revision;
            if (notify) foreach (var change in changes!) _notifications.Enqueue(change);
            RecycleStore(original);
        }
        catch { var abandoned = Store; Store = original; EncodedSize = originalSize; RecycleStore(abandoned); throw; }
    }
    internal override void ConnectHook(object owner, MethodInfo? hook) => _hook = hook == null ? null :
        (SyncChanged<TChange>)Delegate.CreateDelegate(typeof(SyncChanged<TChange>), owner, hook);
    internal override void ClearNotifications() { _notifications.Clear(); _hook = null; }
    internal override void Notify()
    {
        if (Interlocked.CompareExchange(ref _notifying, 1, 0) != 0) return;
        while (true)
        {
            TChange change; CollectionBinding? binding;
            lock (Gate)
            {
                binding = Binding;
                if (binding == null || !binding.Active || _notifications.Count == 0)
                { _notifications.Clear(); Volatile.Write(ref _notifying, 0); return; }
                change = _notifications.Dequeue();
                Volatile.Write(ref _notificationThread, Environment.CurrentManagedThreadId);
            }
            try
            {
                Invoke(_hook, in change, binding);
                foreach (var handler in Volatile.Read(ref _listeners)) Invoke(handler, in change, binding);
            }
            finally { Volatile.Write(ref _notificationThread, 0); }
        }
    }
    private static void Invoke(SyncChanged<TChange>? callback, in TChange change, CollectionBinding binding)
    {
        if (!binding.Active || callback == null) return;
        try { callback(in change); } catch (Exception ex) { binding.Runtime.ReportSyncError(ex); }
    }
    protected static TChange[] Bounded(IEnumerable<TChange> changes)
    {
        if (changes == null) throw new ArgumentNullException(nameof(changes));
        var result = new List<TChange>();
        foreach (var change in changes)
        { if (result.Count == SyncWire.MaxOperations) throw new RpcException(RpcError.LimitExceeded, "Collection batch exceeds 64 operations"); result.Add(change); }
        return result.ToArray();
    }
}
}
