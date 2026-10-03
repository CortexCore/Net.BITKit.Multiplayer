using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BITKit.Multiplayer
{
public sealed partial class RpcRuntime
{
    private readonly Queue<(CollectionBinding Binding, Packet Packet)> _syncWrites = new();
    private readonly Dictionary<(TargetKey, string), long> _syncRequests = new();
    private int _syncWriteCount;
    private bool _syncWriter;
    /// <summary>Once per binding after every declared state member has an initial value. Runs outside runtime locks.</summary>
    public event Action<TargetKey>? Synchronized;
    /// <summary>Binding teardown notification; invoked outside bookkeeping locks.</summary>
    public event Action<TargetKey>? TargetUnbound;
    public bool IsStateSynchronized(TargetKey key)
    {
        lock (_gate) return _bindings.TryGetValue(key, out var bound) && bound.Active && !bound.Initializing &&
            (bound.StateFingerprints.Count == 0 || bound.Synchronized);
    }
    private void NotifyTargetUnbound(TargetKey key)
    {
        var handlers = TargetUnbound;
        if (handlers != null) foreach (Action<TargetKey> handler in handlers.GetInvocationList())
            try { handler(key); } catch (Exception ex) { ReportUnhandled(ex); }
    }
    private int StateEntryCountLocked() => _state.Count + _bindings.Values.Sum(b =>
        b.Collections.Keys.Count(name => !_state.ContainsKey((b.Key, name))));
    internal void ReportSyncError(Exception exception) => ReportUnhandled(exception);
    private byte[] EncodeState(Bound bound, string property, object? value) => SyncWire.Wrap(0, bound.StateFingerprints[property], -1,
        ReliableValues.Encode(bound.Properties[property].Property.PropertyType, value, MaxStateValueBytes - SyncWire.Header));
    private object? DecodeState(Bound bound, string property, byte[] value)
    {
        var bytes = SyncWire.Open(value, 0, bound.StateFingerprints[property], out var basis);
        if (basis != -1) throw new RpcException(RpcError.InvalidPayload, "Invalid scalar state header");
        return ReliableValues.Decode(bound.Properties[property].Property.PropertyType, bytes.ToArray(), MaxStateValueBytes - SyncWire.Header);
    }
    private void InvokeStateHook(Bound bound, string property, object? oldValue, object? newValue)
    {
        if (!bound.Active || !bound.StateHooks.TryGetValue(property, out var hook)) return;
        try { hook(bound.Instance, oldValue, newValue); } catch (Exception ex) { ReportUnhandled(ex); }
    }
    private void NotifySynchronized(Bound bound)
    {
        lock (_gate)
        {
            if (!bound.Active || bound.Initializing || bound.Synchronized || bound.StateFingerprints.Count == 0 ||
                bound.Versions.Count != bound.StateFingerprints.Count) return;
            bound.Synchronized = true;
        }
        var handlers = Synchronized;
        if (handlers != null) foreach (Action<TargetKey> handler in handlers.GetInvocationList())
            try { handler(bound.Key); } catch (Exception ex) { ReportUnhandled(ex); }
    }
    private void AttachCollections(Bound bound) // _gate held
    {
        foreach (var pair in bound.Collections)
        {
            var collection = pair.Value;
            lock (collection.Gate)
            {
                if (collection.Binding != null) throw new InvalidOperationException("Collection already bound");
                collection.ConnectHook(bound.Instance, bound.CollectionHooks.TryGetValue(pair.Key, out var hook) ? hook : null);
                collection.Binding = new CollectionBinding { Runtime = this, Key = bound.Key, Token = bound.Token,
                    Property = pair.Key, Fingerprint = bound.StateFingerprints[pair.Key], Active = true };
                collection.EverBound = true; collection.Revision = IsHost && !_state.ContainsKey((bound.Key, pair.Key)) ? 0 : -1;
                if (IsHost && !_state.ContainsKey((bound.Key, pair.Key))) bound.Versions[pair.Key] = 0;
            }
        }
    }
    private void DetachCollections(Bound bound, bool retain) // _gate held; consistent gate -> collection lock order
    {
        foreach (var pair in bound.Collections)
        {
            var collection = pair.Value;
            lock (collection.Gate)
            {
                var binding = collection.Binding;
                if (binding == null || !ReferenceEquals(binding.Token, bound.Token)) continue;
                try
                {
                    if (retain && collection.Revision >= 0)
                        _state[(bound.Key, pair.Key)] = (collection.Revision, collection.Snapshot(binding.Fingerprint));
                }
                finally { binding.Active = false; collection.Binding = null; collection.ClearNotifications(); }
            }
            _syncRequests.Remove((bound.Key, pair.Key));
        }
        // Drop queued work immediately. The already-dequeued I/O frame remains owned
        // by its actual Send task until completion, including during runtime Dispose.
        int queued = _syncWrites.Count;
        for (int i = 0; i < queued; i++)
        {
            var item = _syncWrites.Dequeue();
            if (ReferenceEquals(item.Binding.Token, bound.Token)) _syncWriteCount--;
            else _syncWrites.Enqueue(item);
        }
    }
    internal bool ChangeCollection<T>(CollectionBinding binding, SyncCollection<T> collection, T change, T[]? batch) where T : struct
    {
        bool start;
        lock (_gate)
        {
            Check(); RequireHost();
            if (!binding.Active || !_bindings.TryGetValue(binding.Key, out var bound) || !bound.Active || bound.Initializing ||
                !ReferenceEquals(bound.Token, binding.Token)) throw new RpcException(RpcError.Disposed, "Collection binding retired");
            if (_syncWriteCount >= 128) throw new RpcException(RpcError.LimitExceeded, "Collection send backlog exceeded");
            lock (collection.Gate)
            {
                if (!ReferenceEquals(collection.Binding, binding)) throw new RpcException(RpcError.Disposed, "Collection rebound");
                var frame = collection.Commit(change, batch, true, out _);
                if (frame == null) return false;
                var packet = StatePacket(binding.Key, binding.Property, collection.Revision, frame); packet.Kind = "stateDelta";
                bound.Versions[binding.Property] = collection.Revision;
                _state.TryRemove((binding.Key, binding.Property), out _); // snapshots materialized on demand, not every edit
                _syncWrites.Enqueue((binding, packet)); _syncWriteCount++;
            }
            start = !_syncWriter; _syncWriter = true;
        }
        // Callbacks and I/O are never invoked while holding either bookkeeping lock.
        collection.Notify();
        if (start) _ = WriteCollections();
        return true;
    }
    private async Task WriteCollections()
    {
        while (true)
        {
            (CollectionBinding Binding, Packet Packet) write;
            IReadOnlyList<RoomMember> peers;
            lock (_gate)
            {
                if (_syncWrites.Count == 0) { _syncWriter = false; return; }
                write = _syncWrites.Dequeue(); peers = _memberSnapshot;
            }
            try
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    var peer = peers[i];
                    if (!write.Binding.Active) break;
                    if (CanReceiveState(peer, write.Binding.Key))
                        try { await Send(peer.Peer, write.Packet).ConfigureAwait(false); }
                        catch (Exception ex) { ReportUnhandled(ex); }
                }
            }
            finally { lock (_gate) _syncWriteCount--; }
        }
    }
    private void SendCollectionSnapshots(RoomMember member, TargetKey? only = null)
    {
        foreach (var bound in _bindings.Values)
        {
            if (only.HasValue && !bound.Key.Equals(only.Value) || !CanReceiveState(member, bound.Key)) continue;
            var packets = new List<Packet>();
            lock (_gate)
            {
                if (!bound.Active) continue;
                foreach (var pair in bound.Collections) lock (pair.Value.Gate)
                    packets.Add(StatePacket(bound.Key, pair.Key, pair.Value.Revision, pair.Value.Snapshot(bound.StateFingerprints[pair.Key])));
            }
            foreach (var packet in packets) if (CanReceiveState(member, bound.Key)) SendBackground(member.Peer, packet);
        }
    }
    private void RequestCollectionSnapshot(TargetKey key, string property)
    {
        object token; long stamp;
        lock (_gate)
        {
            if (!IsClient || !IsReady || _disposed != 0 || !_bindings.TryGetValue(key, out var bound) || !bound.Active) return;
            var identity = (key, property); long now = DateTime.UtcNow.Ticks;
            if (_syncRequests.TryGetValue(identity, out var last) && now - last < TimeSpan.FromMilliseconds(500).Ticks) return;
            if (!_syncRequests.ContainsKey(identity) && _syncRequests.Count >= 128)
                throw new RpcException(RpcError.LimitExceeded, "State resynchronization backlog exceeded");
            _syncRequests[identity] = stamp = now; token = bound.Token;
        }
        SendBackground(HostPeerId, new Packet { Kind = "snapshotRequest", Service = key.Service, Entity = key.Entity,
            Component = key.Component, Property = property });
        _ = RetrySnapshot(key, property, token, stamp);
    }
    private async Task RetrySnapshot(TargetKey key, string property, object token, long stamp)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                    if (_disposed != 0 || !IsReady || !_bindings.TryGetValue(key, out var bound) || !bound.Active ||
                        !ReferenceEquals(bound.Token, token) || !_syncRequests.TryGetValue((key, property), out var current) || current != stamp) return;
                if (attempt == 2) { ReportUnhandled(new RpcException(RpcError.Timeout, "Collection snapshot recovery exhausted: " + property)); return; }
                SendBackground(HostPeerId, new Packet { Kind = "snapshotRequest", Service = key.Service, Entity = key.Entity,
                    Component = key.Component, Property = property });
            }
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>Explicit idempotent state recovery for an admitted Client; updates apply to the existing objects.</summary>
    public void RequestStateSnapshot(TargetKey key)
    {
        lock (_gate)
        {
            Check();
            if (!IsClient || !IsReady) throw new RpcException(RpcError.InvalidRole, "Ready Client required");
            if (!_bindings.ContainsKey(key)) throw new RpcException(RpcError.MissingTarget, "State target not bound");
        }
        SendBackground(HostPeerId, new Packet { Kind = "snapshotRequest", Service = key.Service, Entity = key.Entity, Component = key.Component });
    }
    private void ReceiveCollection(Packet packet, bool delta)
    {
        if (packet.Property == null || packet.Version < 0 || packet.Value == null)
            throw new RpcException(RpcError.InvalidPayload, "Invalid collection envelope");
        SyncCollection? collection = null; Bound? bound = null; bool gap = false;
        lock (_gate)
        {
            if (_disposed != 0 || _removedTargets.Contains(packet.Key)) return;
            if (!_bindings.TryGetValue(packet.Key, out bound))
            {
                // No unbounded delta queue for late bind. A new bind requests current state.
                if (delta) _state.TryRemove((packet.Key, packet.Property), out _);
                return;
            }
            if (!bound.Collections.TryGetValue(packet.Property, out collection))
                throw new RpcException(RpcError.InvalidPayload, "Unknown collection member; incompatible state contract");
            if (!bound.Active) return;
            lock (collection.Gate)
            {
                SyncWire.Open(packet.Value, collection.Kind, bound.StateFingerprints[packet.Property], out var basis);
                if (!delta && basis != -1) throw new RpcException(RpcError.InvalidPayload, "Invalid collection snapshot base");
                if (packet.Version <= collection.Revision)
                {
                    if (!delta && packet.Version == collection.Revision) _syncRequests.Remove((packet.Key, packet.Property));
                    return;
                }
                gap = delta && (collection.Revision < 0 || basis != collection.Revision);
                if (!gap)
                {
                    collection.ApplyFrame(packet.Value, bound.StateFingerprints[packet.Property], packet.Version, delta);
                    bound.Versions[packet.Property] = packet.Version;
                    _state.TryRemove((packet.Key, packet.Property), out _);
                    _syncRequests.Remove((packet.Key, packet.Property));
                }
            }
        }
        if (gap) { RequestCollectionSnapshot(packet.Key, packet.Property); return; }
        collection.Notify(); NotifyState(packet.Key, packet.Property); NotifySynchronized(bound);
    }
}
}
