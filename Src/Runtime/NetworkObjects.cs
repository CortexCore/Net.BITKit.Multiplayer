using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc
{
    public enum NetworkObjectKind : byte { Prefab = 1, Scene = 2 }

    public sealed class NetworkObjectState
    {
        public uint EntityId { get; set; }
        public uint OwnerPeerId { get; set; }
        public uint WorldGeneration { get; set; }
        public ulong Revision { get; set; }
        public NetworkObjectKind Kind { get; set; }
        /// <summary>Host-derived ready contract. Clients must attach a NetEntity before becoming ready when true.</summary>
        public bool RequiresEntityState { get; set; }
        public string? Address { get; set; }
        public string? SceneKey { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float RotationW { get; set; } = 1f;
        public bool Active { get; set; } = true;
        public NetworkObjectState Copy() => (NetworkObjectState)MemberwiseClone();
    }

    public sealed class NetworkObjectSnapshot
    {
        public uint WorldGeneration { get; set; }
        public ulong Revision { get; set; }
        public NetworkObjectState[] Objects { get; set; } = Array.Empty<NetworkObjectState>();
    }

    /// <summary>An engine instance lease. Core owns the optional entity's registration and teardown.</summary>
    public sealed class NetworkObjectInstance
    {
        private NetEntity? _entity;
        private bool _frozen;
        public NetworkObjectInstance(object instance, INetworkIdentity identity, NetEntity? entity = null)
        { Instance = instance ?? throw new ArgumentNullException(nameof(instance)); Identity = identity ?? throw new ArgumentNullException(nameof(identity)); _entity = entity; }
        public object Instance { get; }
        public INetworkIdentity Identity { get; }
        public NetEntity? Entity
        {
            get => _entity;
            set { if (_frozen) throw new InvalidOperationException("Attach the entity during Initializing, before registration."); _entity = value; }
        }
        internal void Freeze() => _frozen = true;
    }

    /// <summary>
    /// Only engine operations live here. Scene lookup may return null until its scene is loaded.
    /// Each load must return a distinct registration lease, even when reusing an authored scene instance.
    /// Release must clear/deactivate only the lease's binding: if the identity now belongs to a different
    /// entity ID, retain that newer binding and clean up only resources owned by the retired lease.
    /// </summary>
    public interface INetworkObjectAdapter
    {
        UniTask<NetworkObjectInstance?> InstantiateAsync(NetworkObjectState state, CancellationToken cancellationToken);
        void Bind(NetworkObjectInstance instance, NetworkObjectState state, bool authority);
        void ApplyState(NetworkObjectInstance instance, NetworkObjectState state);
        void Release(NetworkObjectInstance instance, NetworkObjectState state);
    }

    /// <summary>Optional engine-thread boundary used after asynchronous loads, hooks and RPC replies.</summary>
    public interface INetworkObjectDispatcher
    {
        UniTask SwitchToEngineThreadAsync(CancellationToken cancellationToken);
    }

    public sealed class NetworkObjectHandle
    {
        private NetworkObjectState _state;
        internal NetworkObjectHandle(NetworkObjectState state, NetworkObjectInstance instance, bool authority)
        { _state = state; Lease = instance; IsAuthority = authority; }
        internal void Update(NetworkObjectState state) => _state = state;
        public NetworkObjectState State => _state.Copy();
        public NetworkObjectInstance Lease { get; }
        public object Instance => Lease.Instance;
        public INetworkIdentity Identity => Lease.Identity;
        public NetEntity? Entity => Lease.Entity;
        public void AttachEntity(NetEntity entity) => Lease.Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        public uint EntityId => _state.EntityId;
        public uint OwnerPeerId => _state.OwnerPeerId;
        public bool IsAuthority { get; }
        public bool IsOwner(uint localPeerId) => localPeerId != 0 && localPeerId == OwnerPeerId;
        public bool IsSceneObject => _state.Kind == NetworkObjectKind.Scene;
        public string? SceneKey => _state.SceneKey;
        public string? PrefabAddress => _state.Address;
    }

    // This built-in protocol uses native descriptors and typed receivers. The Core assembly is deliberately
    // excluded from Unity ILPP and is not woven by the .NET project; ordinary source RPC bodies would be local.
    internal interface INetworkObjectProtocol
    {
        [Rpc(SendTo.Host)] UniTask<NetworkObjectSnapshot> ReadSnapshot(uint worldGeneration);
        [Rpc(SendTo.Host)] UniTask<byte[]> ReadEntityState(uint worldGeneration, uint entityId);
        [Rpc(SendTo.All)] void Upsert(NetworkObjectState state);
        [Rpc(SendTo.All)] void ChangeOwner(uint worldGeneration, uint entityId, uint ownerPeerId, ulong revision);
        [Rpc(SendTo.All)] void Remove(uint worldGeneration, uint entityId, ulong revision);
    }

    /// <summary>
    /// Host-authoritative object roster for one world and runtime scope. IDs are never reused; a new world
    /// requires a new service/runtime scope. Engines provide only instance and identity operations.
    /// </summary>
    public sealed class NetworkObjectService : IDisposable, INetworkObjectProtocol
    {
        private sealed class Entry
        {
            public NetworkObjectState State = null!;
            public NetworkObjectHandle? Handle;
            public NetworkObjectInstance? Supplied;
            public NetworkObjectInstance? Instance;
            public UniTaskCompletionSource<NetworkObjectHandle?>? Loading;
            public CancellationTokenSource? Cancellation;
            public bool Published;
        }
        private readonly struct PendingOwner
        {
            public PendingOwner(uint owner, ulong revision) { Owner = owner; Revision = revision; }
            public uint Owner { get; }
            public ulong Revision { get; }
        }
        private static readonly Type Protocol = typeof(INetworkObjectProtocol);
        private static readonly uint TargetId = RpcContextService.ContractId(Protocol);
        private static uint MethodId(string name) => RpcContextService.RpcMethodId(Protocol, Protocol.GetMethod(name)!);
        private static readonly uint ReadSnapshotId = MethodId(nameof(INetworkObjectProtocol.ReadSnapshot));
        private static readonly uint ReadEntityId = MethodId(nameof(INetworkObjectProtocol.ReadEntityState));
        private static readonly uint UpsertId = MethodId(nameof(INetworkObjectProtocol.Upsert));
        private static readonly uint OwnerId = MethodId(nameof(INetworkObjectProtocol.ChangeOwner));
        private static readonly uint RemoveId = MethodId(nameof(INetworkObjectProtocol.Remove));
        private readonly object _gate = new object();
        private readonly RpcContextService _runtime;
        private readonly RpcContext _context;
        private readonly INetworkObjectAdapter _adapter;
        private readonly IEntitiesService _entities;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenRegistration _sessionCancellation;
        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly Dictionary<uint, ulong> _tombstones = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, PendingOwner> _pendingOwners = new Dictionary<uint, PendingOwner>();
        private uint _nextEntityId;
        private ulong _revision, _snapshotFloor;
        private bool _disposed;

        public NetworkObjectService(RpcContextService runtime, INetworkObjectAdapter adapter, IEntitiesService entities,
            uint worldGeneration, uint localPeerId, CancellationToken lifetime = default)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _entities = entities ?? throw new ArgumentNullException(nameof(entities));
            if (!ReferenceEquals(runtime.Entities, entities))
                throw new ArgumentException("Use the entity registry already attached to this runtime.", nameof(entities));
            if (worldGeneration == 0) throw new ArgumentOutOfRangeException(nameof(worldGeneration));
            if (localPeerId == 0 || runtime.IsServer && localPeerId != 1 || !runtime.IsServer && localPeerId == 1)
                throw new ArgumentOutOfRangeException(nameof(localPeerId), "Host is peer 1; Clients have distinct nonzero IDs.");
            WorldGeneration = worldGeneration; LocalPeerId = localPeerId;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            try { _context = runtime.RegisterNetworkObjectService(TargetId, this, Protocol); }
            catch { _lifetime.Dispose(); throw; }
            RegisterReceivers();
            _sessionCancellation = lifetime.Register(() => DisposeOnEngineThread().Forget(Report));
        }

        public bool IsAuthority => _runtime.IsServer;
        public uint WorldGeneration { get; }
        public uint LocalPeerId { get; }
        public IReadOnlyCollection<NetworkObjectHandle> Objects
        { get { lock (_gate) return _entries.Values.Where(entry => entry.Handle != null).Select(entry => entry.Handle!).ToArray(); } }
        public event Func<NetworkObjectHandle, CancellationToken, UniTask>? Initializing;
        public event Action<NetworkObjectHandle>? Spawned;
        public event Action<NetworkObjectHandle>? Despawning;
        public event Action<Exception>? Faulted;

        public UniTask<NetworkObjectHandle> SpawnAsync(NetworkObjectState template, CancellationToken cancellationToken = default) =>
            SpawnCore(template, null, cancellationToken);
        public UniTask<NetworkObjectHandle> SpawnAsync(NetworkObjectState template, NetworkObjectInstance supplied, CancellationToken cancellationToken = default) =>
            SpawnCore(template, supplied ?? throw new ArgumentNullException(nameof(supplied)), cancellationToken);

        private async UniTask<NetworkObjectHandle> SpawnCore(NetworkObjectState template, NetworkObjectInstance? supplied, CancellationToken token)
        {
            Entry? entry = null; bool created = false, suppliedAlreadyOwned = false;
            try
            {
                if (template == null) throw new ArgumentNullException(nameof(template));
                token.ThrowIfCancellationRequested();
                await SwitchToEngineThread(token);
                lock (_gate)
                {
                    CheckAuthority();
                    var state = template.Copy();
                    state.RequiresEntityState = false; // Determined from the Host lease after Initializing.
                    state.EntityId = 1; state.WorldGeneration = WorldGeneration; state.Revision = 1; Validate(state);
                    entry = state.Kind == NetworkObjectKind.Scene
                        ? _entries.Values.FirstOrDefault(item => item.State.Kind == NetworkObjectKind.Scene && item.State.SceneKey == state.SceneKey) : null;
                    if (entry != null)
                    {
                        var existing = entry.Handle?.Lease ?? entry.Instance ?? entry.Supplied;
                        if (supplied != null && (existing == null || !ReferenceEquals(existing.Instance, supplied.Instance)))
                            throw new InvalidOperationException("Duplicate network scene key: " + state.SceneKey);
                        suppliedAlreadyOwned = supplied != null;
                    }
                    else
                    {
                        if (supplied != null && IsManagedInstance(supplied.Instance))
                            throw new InvalidOperationException("The engine instance already belongs to a network object.");
                        if (_nextEntityId == uint.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Network entity ID space exhausted.");
                        state.EntityId = ++_nextEntityId; state.Revision = NextRevision();
                        entry = new Entry { State = state, Supplied = supplied };
                        _entries.Add(state.EntityId, entry); created = true;
                    }
                }
                var handle = await EnsureLoaded(entry, token);
                token.ThrowIfCancellationRequested();
                if (handle == null) throw new InvalidOperationException("The network object instance is not available.");
                if (created)
                {
                    NetworkObjectState state;
                    lock (_gate) { RequireCurrent(entry); state = entry.State.Copy(); }
                    BroadcastUpsert(state);
                }
                return handle;
            }
            catch
            {
                await SwitchToEngineThread(CancellationToken.None);
                lock (_gate)
                {
                    if (created && entry != null) { if (IsCurrent(entry)) RemoveLocal(entry.State.EntityId, NextRevision()); }
                    else if (supplied != null && !suppliedAlreadyOwned && !IsManagedInstance(supplied.Instance))
                    {
                        try { _adapter.Release(supplied, template?.Copy() ?? new NetworkObjectState { Kind = NetworkObjectKind.Prefab }); }
                        catch (Exception error) { Report(error); }
                    }
                }
                throw;
            }
        }
        private bool IsManagedInstance(object instance) => _entries.Values.Any(entry =>
            ReferenceEquals((entry.Handle?.Lease ?? entry.Instance ?? entry.Supplied)?.Instance, instance));

        public async UniTask SynchronizeAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) { ThrowIfDisposed(); if (IsAuthority) throw new RpcException(RpcError.InvalidRole, "Host already owns the object roster."); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            NetworkObjectSnapshot snapshot;
            using (var bag = NetMessageBag.Pool())
            {
                bag.Write(WorldGeneration);
                snapshot = await _context.Request<NetworkObjectSnapshot>(Model(ReadSnapshotId, bag), linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            ValidateSnapshot(snapshot);
            await SwitchToEngineThread(linked.Token);
            Entry[] pending;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (snapshot.Revision >= _snapshotFloor)
                {
                    var included = new HashSet<uint>();
                    foreach (var state in snapshot.Objects)
                    { included.Add(state.EntityId); MergeState(state, true); }
                    foreach (var stale in _entries.Values.Where(entry => entry.State.Revision <= snapshot.Revision && !included.Contains(entry.State.EntityId)).ToArray())
                        RemoveLocal(stale.State.EntityId, snapshot.Revision);
                    foreach (var stale in _pendingOwners.Where(item => item.Value.Revision <= snapshot.Revision).Select(item => item.Key).ToArray()) _pendingOwners.Remove(stale);
                    _snapshotFloor = snapshot.Revision; _revision = Math.Max(_revision, snapshot.Revision);
                }
                pending = _entries.Values.Where(entry => entry.Handle == null).ToArray();
            }
            foreach (var entry in pending) await EnsureLoaded(entry, linked.Token);
        }

        /// <summary>Retries failed loads and scene objects that were not loaded when their roster arrived.</summary>
        public async UniTask RetryPendingAsync(CancellationToken cancellationToken = default)
        {
            Entry[] pending;
            lock (_gate) { ThrowIfDisposed(); pending = _entries.Values.Where(entry => entry.Handle == null).ToArray(); }
            foreach (var entry in pending) await EnsureLoaded(entry, cancellationToken);
        }

        /// <summary>Host only. Engine integrations call this synchronous mutation on their engine thread.</summary>
        public void SetOwner(uint entityId, uint ownerPeerId)
        {
            NetworkObjectState state; bool published;
            lock (_gate)
            {
                CheckAuthority();
                if (!_entries.TryGetValue(entityId, out var entry)) throw new KeyNotFoundException("Network object is not registered.");
                state = entry.State.Copy(); state.OwnerPeerId = ownerPeerId; state.Revision = NextRevision();
                entry.State = state; ApplyHandle(entry); published = entry.Published;
            }
            if (published)
            {
                using var call = Begin(OwnerId); call.Write(WorldGeneration); call.Write(entityId); call.Write(ownerPeerId); call.Write(state.Revision); call.FinishVoid();
            }
        }

        /// <summary>Host only. Engine integrations call this synchronous teardown on their engine thread.</summary>
        public UniTask DespawnAsync(uint entityId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ulong revision; bool published;
            lock (_gate)
            {
                CheckAuthority();
                if (!_entries.TryGetValue(entityId, out var entry)) return UniTask.CompletedTask;
                published = entry.Published; revision = NextRevision(); RemoveLocal(entityId, revision);
            }
            if (published) { using var call = Begin(RemoveId); call.Write(WorldGeneration); call.Write(entityId); call.Write(revision); call.FinishVoid(); }
            return UniTask.CompletedTask;
        }

        public bool TryGet(uint entityId, [NotNullWhen(true)] out NetworkObjectHandle? handle)
        { lock (_gate) { handle = _entries.TryGetValue(entityId, out var entry) ? entry.Handle : null; return handle != null; } }
        public bool Owns(uint entityId)
        { lock (_gate) return _entries.TryGetValue(entityId, out var entry) && entry.State.OwnerPeerId == LocalPeerId; }

        private UniTask<NetworkObjectHandle?> EnsureLoaded(Entry entry, CancellationToken token)
        {
            UniTaskCompletionSource<NetworkObjectHandle?> completion; bool start = false;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); ThrowIfDisposed();
                if (!IsCurrent(entry)) return UniTask.FromResult<NetworkObjectHandle?>(null);
                if (entry.Handle != null) return UniTask.FromResult<NetworkObjectHandle?>(entry.Handle);
                if (entry.Loading == null)
                {
                    entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
                    entry.Loading = new UniTaskCompletionSource<NetworkObjectHandle?>(); start = true;
                }
                completion = entry.Loading;
            }
            if (start) Load(entry, completion, entry.Cancellation!).Forget(Report);
            return completion.Task.AttachExternalCancellation(token);
        }

        private async UniTask Load(Entry entry, UniTaskCompletionSource<NetworkObjectHandle?> completion, CancellationTokenSource cancellation)
        {
            NetworkObjectInstance? instance = null; NetworkObjectHandle? handle = null;
            bool registered = false, committed = false;
            Exception? failure = null;
            var token = cancellation.Token;
            try
            {
                NetworkObjectState state;
                lock (_gate) { RequireCurrent(entry); state = entry.State.Copy(); instance = entry.Supplied; entry.Supplied = null; entry.Instance = instance; }
                if (instance == null) instance = await _adapter.InstantiateAsync(state, token);
                lock (_gate) entry.Instance = instance;
                token.ThrowIfCancellationRequested();
                if (instance == null)
                {
                    if (state.Kind != NetworkObjectKind.Scene) throw new InvalidOperationException("Prefab adapter returned no instance for " + state.Address);
                    return;
                }
                await SwitchToEngineThread(token);
                lock (_gate)
                {
                    RequireCurrent(entry); state = entry.State;
                    _adapter.Bind(instance, state.Copy(), IsAuthority);
                    RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    if (instance.Identity.EntityId != state.EntityId) throw new InvalidOperationException("Adapter did not bind the network identity to the assigned entity ID.");
                    handle = new NetworkObjectHandle(state, instance, IsAuthority);
                }
                var callbacks = Initializing;
                if (callbacks != null)
                    foreach (Func<NetworkObjectHandle, CancellationToken, UniTask> callback in callbacks.GetInvocationList())
                    { await callback(handle, token); token.ThrowIfCancellationRequested(); await SwitchToEngineThread(token); lock (_gate) RequireCurrent(entry); }
                await SwitchToEngineThread(token);
                lock (_gate)
                {
                    RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    if (!IsAuthority && entry.State.RequiresEntityState && instance.Entity == null)
                        throw new InvalidOperationException("This network object requires a NetEntity. Attach it during Initializing before the object can become ready.");
                    instance.Freeze();
                    if (instance.Entity != null)
                    {
                        var identity = instance.Entity.ServiceProvider.GetService(typeof(INetworkIdentity)) as INetworkIdentity;
                        if (identity == null || identity.EntityId != entry.State.EntityId)
                            throw new InvalidOperationException("The attached NetEntity must resolve this object's INetworkIdentity.");
                        if (!_entities.Entities.Contains(instance.Entity)) _entities.Register(instance.Entity);
                        registered = true;
                        RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    }
                }
                byte[]? initialState = null;
                if (!IsAuthority && instance.Entity != null)
                {
                    using var bag = NetMessageBag.Pool(); bag.Write(WorldGeneration); bag.Write(entry.State.EntityId);
                    initialState = await _context.Request<byte[]>(Model(ReadEntityId, bag), token);
                }
                await SwitchToEngineThread(token);
                lock (_gate)
                {
                    RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    if (initialState != null) _runtime.ApplyEntityState(instance.Entity!, initialState);
                    RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    if (IsAuthority)
                    {
                        var ready = entry.State.Copy(); ready.Revision = NextRevision(); ready.RequiresEntityState = instance.Entity != null;
                        entry.State = ready; entry.Published = true;
                    }
                    handle.Update(entry.State); _adapter.ApplyState(instance, entry.State.Copy());
                    RequireCurrent(entry); token.ThrowIfCancellationRequested();
                    handle.Update(entry.State);
                    entry.Handle = handle; committed = true;
                    Raise(Spawned, handle);
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (!committed && instance != null)
                {
                    try
                    {
                        await SwitchToEngineThread(CancellationToken.None);
                        try { if (instance.Entity != null && (registered || _entities.Entities.Contains(instance.Entity))) _entities.Unregister(instance.Entity); }
                        catch (Exception error) { Report(error); }
                        _adapter.Release(instance, handle?.State ?? entry.State.Copy());
                    }
                    catch (Exception error) { Report(error); }
                }
                lock (_gate)
                {
                    if (ReferenceEquals(entry.Loading, completion)) { entry.Loading = null; entry.Cancellation = null; entry.Instance = null; }
                }
                cancellation.Dispose();
                // Publish completion only after cleanup and clearing Loading, so a caller may retry immediately.
                if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult(handle);
            }
        }

        private Entry? MergeState(NetworkObjectState incoming, bool snapshot = false)
        {
            Validate(incoming);
            if (_tombstones.ContainsKey(incoming.EntityId)) return null; // IDs are never reused in this world.
            if (!snapshot && incoming.Revision <= _snapshotFloor) return _entries.TryGetValue(incoming.EntityId, out var known) ? known : null;
            if (_entries.TryGetValue(incoming.EntityId, out var entry))
            {
                if (entry.State.Kind != incoming.Kind || entry.State.Address != incoming.Address || entry.State.SceneKey != incoming.SceneKey ||
                    entry.State.RequiresEntityState != incoming.RequiresEntityState)
                    throw new RpcException(RpcError.InvalidPayload, "Network object identity cannot change within one world.");
                if (entry.State.Revision >= incoming.Revision) return entry;
            }
            var state = incoming.Copy();
            if (_pendingOwners.TryGetValue(state.EntityId, out var owner))
            {
                if (owner.Revision > state.Revision) { state.OwnerPeerId = owner.Owner; state.Revision = owner.Revision; }
                _pendingOwners.Remove(state.EntityId);
            }
            if (entry == null) { entry = new Entry(); _entries.Add(state.EntityId, entry); }
            entry.State = state; entry.Published = true; _revision = Math.Max(_revision, state.Revision); ApplyHandle(entry);
            return entry;
        }

        private void ApplyHandle(Entry entry)
        {
            if (entry.Handle == null) return;
            entry.Handle.Update(entry.State); _adapter.ApplyState(entry.Handle.Lease, entry.State.Copy());
        }

        private void RemoveLocal(uint entityId, ulong revision)
        {
            _tombstones[entityId] = Math.Max(_tombstones.TryGetValue(entityId, out var old) ? old : 0, revision);
            _pendingOwners.Remove(entityId);
            if (!_entries.Remove(entityId, out var entry)) return;
            try { entry.Cancellation?.Cancel(); } catch (Exception error) { Report(error); }
            if (entry.Handle != null)
            {
                Raise(Despawning, entry.Handle);
                try { if (entry.Handle.Entity != null) _entities.Unregister(entry.Handle.Entity); } catch (Exception error) { Report(error); }
                try { _adapter.Release(entry.Handle.Lease, entry.State.Copy()); } catch (Exception error) { Report(error); }
                entry.Handle = null;
            }
            else if (entry.Supplied != null)
            {
                try { _adapter.Release(entry.Supplied, entry.State.Copy()); } catch (Exception error) { Report(error); }
                entry.Supplied = null;
            }
        }

        UniTask<NetworkObjectSnapshot> INetworkObjectProtocol.ReadSnapshot(uint generation)
        {
            lock (_gate)
            {
                CheckAuthority(); CheckGeneration(generation);
                return UniTask.FromResult(new NetworkObjectSnapshot { WorldGeneration = WorldGeneration, Revision = _revision,
                    Objects = _entries.Values.Where(entry => entry.Published).OrderBy(entry => entry.State.EntityId).Select(entry => entry.State.Copy()).ToArray() });
            }
        }
        async UniTask<byte[]> INetworkObjectProtocol.ReadEntityState(uint generation, uint entityId)
        {
            Entry entry;
            lock (_gate)
            {
                CheckAuthority(); CheckGeneration(generation);
                if (!_entries.TryGetValue(entityId, out entry!) || !entry.Published || entry.Handle?.Entity == null)
                    throw new RpcException(RpcError.MissingTarget, "Network entity is not ready.");
            }
            var result = await _runtime.CaptureEntityStateAsync(entityId, _lifetime.Token, SwitchToEngineThread);
            lock (_gate) RequireCurrent(entry);
            return result;
        }
        void INetworkObjectProtocol.Upsert(NetworkObjectState state)
        {
            Entry? entry;
            lock (_gate)
            {
                if (_disposed || IsAuthority || state == null || state.WorldGeneration != WorldGeneration) return;
                entry = MergeState(state);
            }
            if (entry != null) ObserveLoad(entry).Forget(Report);
        }
        void INetworkObjectProtocol.ChangeOwner(uint generation, uint entityId, uint ownerPeerId, ulong revision)
        {
            lock (_gate)
            {
                if (_disposed || IsAuthority || generation != WorldGeneration || entityId == 0 || revision == 0 || revision <= _snapshotFloor || _tombstones.ContainsKey(entityId)) return;
                if (_entries.TryGetValue(entityId, out var entry))
                {
                    if (entry.State.Revision >= revision) return;
                    var state = entry.State.Copy(); state.OwnerPeerId = ownerPeerId; state.Revision = revision; entry.State = state; ApplyHandle(entry);
                }
                else if (!_pendingOwners.TryGetValue(entityId, out var old) || revision > old.Revision) _pendingOwners[entityId] = new PendingOwner(ownerPeerId, revision);
                _revision = Math.Max(_revision, revision);
            }
        }
        void INetworkObjectProtocol.Remove(uint generation, uint entityId, ulong revision)
        {
            lock (_gate)
            {
                if (_disposed || IsAuthority || generation != WorldGeneration || entityId == 0 || revision == 0 || revision <= _snapshotFloor) return;
                if (_entries.TryGetValue(entityId, out var entry) && entry.State.Revision >= revision) return;
                _revision = Math.Max(_revision, revision); RemoveLocal(entityId, revision);
            }
        }
        private async UniTask ObserveLoad(Entry entry)
        { try { await EnsureLoaded(entry, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception error) { Report(error); } }

        private void RegisterReceivers()
        {
            _runtime.SetGeneratedReceiver(TargetId, ReadSnapshotId, (instance, reader) =>
            { var generation = reader.Read<uint>(); reader.Complete(); return NetRpcResults.CompleteUniTask(((INetworkObjectProtocol)instance).ReadSnapshot(generation)); });
            _runtime.SetGeneratedReceiver(TargetId, ReadEntityId, (instance, reader) =>
            { var generation = reader.Read<uint>(); var entity = reader.Read<uint>(); reader.Complete(); return NetRpcResults.CompleteUniTask(((INetworkObjectProtocol)instance).ReadEntityState(generation, entity)); });
            _runtime.SetGeneratedReceiver(TargetId, UpsertId, (instance, reader) =>
            { var state = reader.Read<NetworkObjectState>(); reader.Complete(); return ((NetworkObjectService)instance).ReceiveOnEngine(() => ((INetworkObjectProtocol)instance).Upsert(state)); });
            _runtime.SetGeneratedReceiver(TargetId, OwnerId, (instance, reader) =>
            { var generation = reader.Read<uint>(); var entity = reader.Read<uint>(); var owner = reader.Read<uint>(); var revision = reader.Read<ulong>(); reader.Complete(); return ((NetworkObjectService)instance).ReceiveOnEngine(() => ((INetworkObjectProtocol)instance).ChangeOwner(generation, entity, owner, revision)); });
            _runtime.SetGeneratedReceiver(TargetId, RemoveId, (instance, reader) =>
            { var generation = reader.Read<uint>(); var entity = reader.Read<uint>(); var revision = reader.Read<ulong>(); reader.Complete(); return ((NetworkObjectService)instance).ReceiveOnEngine(() => ((INetworkObjectProtocol)instance).Remove(generation, entity, revision)); });
        }
        private async UniTask<NetMessageBag?> ReceiveOnEngine(Action apply)
        { await SwitchToEngineThread(CancellationToken.None); apply(); return null; }
        private NetRpcModel Model(uint method, NetMessageBag bag) => new NetRpcModel(NetRpcMessageKind.Call, TargetId, method, 0, bag.Count, bag.Memory, _runtime.Scope);
        private NetRpcInvocation Begin(uint method) => NetRpcDispatch.Begin(this, method, SendTo.All, RpcDelivery.Reliable);
        private void BroadcastUpsert(NetworkObjectState state) { using var call = Begin(UpsertId); call.Write(state); call.FinishVoid(); }
        private UniTask SwitchToEngineThread(CancellationToken token) => _adapter is INetworkObjectDispatcher dispatcher ? dispatcher.SwitchToEngineThreadAsync(token) : UniTask.CompletedTask;
        private bool IsCurrent(Entry entry) => !_disposed && _entries.TryGetValue(entry.State.EntityId, out var current) && ReferenceEquals(current, entry);
        private void RequireCurrent(Entry entry) { if (!IsCurrent(entry)) throw new OperationCanceledException("Network object was removed or its world was disposed."); }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(NetworkObjectService)); }
        private void CheckAuthority() { ThrowIfDisposed(); if (!IsAuthority) throw new RpcException(RpcError.InvalidRole, "Only Host may change the network object roster."); }
        private void CheckGeneration(uint generation) { if (generation != WorldGeneration) throw new RpcException(RpcError.InvalidPayload, "Network object world generation mismatch."); }
        private ulong NextRevision() { if (_revision == ulong.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Network object revision exhausted."); return ++_revision; }
        private void ValidateSnapshot(NetworkObjectSnapshot snapshot)
        {
            if (snapshot == null) throw new RpcException(RpcError.InvalidPayload, "Missing network object snapshot.");
            CheckGeneration(snapshot.WorldGeneration);
            if (snapshot.Objects == null) throw new RpcException(RpcError.InvalidPayload, "Missing network object roster.");
            var ids = new HashSet<uint>(); var scenes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in snapshot.Objects)
            {
                Validate(state);
                if (state.Revision > snapshot.Revision || !ids.Add(state.EntityId) || state.Kind == NetworkObjectKind.Scene && !scenes.Add(state.SceneKey!))
                    throw new RpcException(RpcError.InvalidPayload, "Invalid network object snapshot revision or duplicate identity.");
            }
        }
        private void Validate(NetworkObjectState state)
        {
            if (state == null || state.EntityId == 0 || state.WorldGeneration != WorldGeneration || state.Revision == 0 ||
                state.Kind != NetworkObjectKind.Prefab && state.Kind != NetworkObjectKind.Scene ||
                state.Kind == NetworkObjectKind.Prefab && string.IsNullOrWhiteSpace(state.Address) ||
                state.Kind == NetworkObjectKind.Scene && string.IsNullOrWhiteSpace(state.SceneKey) ||
                !Finite(state.PositionX) || !Finite(state.PositionY) || !Finite(state.PositionZ) ||
                !Finite(state.RotationX) || !Finite(state.RotationY) || !Finite(state.RotationZ) || !Finite(state.RotationW))
                throw new RpcException(RpcError.InvalidPayload, "Invalid network object state.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void Raise(Action<NetworkObjectHandle>? handlers, NetworkObjectHandle handle)
        { if (handlers != null) foreach (Action<NetworkObjectHandle> handler in handlers.GetInvocationList()) try { handler(handle); } catch (Exception error) { Report(error); } }
        private void Report(Exception error) { try { Faulted?.Invoke(error); } catch { } }
        private async UniTask DisposeOnEngineThread()
        { await SwitchToEngineThread(CancellationToken.None); Dispose(); }
        /// <summary>For engine adapters, call on the engine thread. Session cancellation is marshaled automatically.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _runtime.RemoveTarget(TargetId, this);
                try { _lifetime.Cancel(); } catch (Exception error) { Report(error); }
                foreach (var id in _entries.Keys.ToArray()) RemoveLocal(id, _revision);
                _pendingOwners.Clear(); _tombstones.Clear();
            }
            _sessionCancellation.Dispose(); _lifetime.Dispose();
        }
    }

    public sealed partial class RpcContextService
    {
        private bool _networkObjectWorldCreated;
        internal RpcContext RegisterNetworkObjectService(uint targetId, NetworkObjectService service, Type protocol)
        {
            lock (_registrationGate)
            {
                if (_networkObjectWorldCreated)
                    throw new InvalidOperationException("A new network object world requires a new runtime scope; entity IDs cannot be reused in this runtime.");
                var context = RegisterTarget(targetId, service, protocol);
                _networkObjectWorldCreated = true;
                return context;
            }
        }
    }
}
