using Cysharp.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.NetRpc
{
    public sealed class NetRpcOptions
    {
        public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMilliseconds(100);
        public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(2);
    }
    public sealed partial class RpcContextService
    {
        private sealed class StateMember
        {
            public uint Target, Property;
            public PropertyInfo Info = null!;
            public Action<NetMessageBag> WriteValue = null!;
            public object? Instance, Value;
            public INetworkState? Collection;
            public byte[]? LastValue;
            public long Revision = -1;
        }
        private sealed class ComponentEntry
        {
            public NetEntity Entity = null!;
            public INetComponent Component = null!;
            public ulong LastFingerprint;
            public long SentRevision = -1, ReceivedRevision = -1;
        }
        private readonly ConcurrentDictionary<(uint Target, uint Property), StateMember> _state = new();
        private readonly ConcurrentDictionary<(uint Entity, uint Component), ComponentEntry> _components = new();
        private readonly ConcurrentDictionary<uint, NetEntity> _networkEntities = new();
        private StateMember[] _stateSnapshot = Array.Empty<StateMember>();
        private KeyValuePair<(uint Entity, uint Component), ComponentEntry>[] _componentSnapshot = Array.Empty<KeyValuePair<(uint Entity, uint Component), ComponentEntry>>();
        private IEntitiesService? _entities;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private CancellationTokenSource? _synchronization;
        public event Action<uint, uint>? StateChanged;
        public void StartSynchronization(NetRpcOptions options)
        {
            if (!IsServer || options.SyncInterval <= TimeSpan.Zero || options.SnapshotInterval < options.SyncInterval || _synchronization != null)
                throw new ArgumentException("Invalid synchronization options or lifecycle.");
            _synchronization = new CancellationTokenSource();
            Observe(Synchronize(options, _synchronization.Token));
        }
        private async UniTask Synchronize(NetRpcOptions options, CancellationToken token)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew(); var nextSnapshot = options.SnapshotInterval;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(options.SyncInterval, token).ConfigureAwait(false);
                    bool force = clock.Elapsed >= nextSnapshot;
                    try { await PublishStateAsync(force); } catch (Exception error) when (!token.IsCancellationRequested) { Report(error); }
                    if (force) nextSnapshot = clock.Elapsed + options.SnapshotInterval;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        internal void RegisterState(uint target, object instance, Type contract)
        {
            if (!contract.IsInterface) return;
            foreach (var property in StateProperties(contract))
            {
                var member = CreateMember(target, contract, property); member.Instance = instance;
                if (member.Collection != null)
                {
                    member.Collection = property.GetValue(instance) as INetworkState ?? throw new InvalidOperationException("Host interface collections must use NetworkList/NetworkDictionary: " + property.Name);
                    member.Collection.Bind(IsServer, (collection, delta) => PublishOperation(member, collection, delta));
                }
                else member.WriteValue = CreateStateWriter(property, instance);
                lock (_stateGate) { _state[(target, member.Property)] = member; Volatile.Write(ref _stateSnapshot, _state.Values.ToArray()); }
            }
        }
        internal void RegisterRemoteState(uint target, Type contract)
        {
            foreach (var property in StateProperties(contract))
            {
                var member = CreateMember(target, contract, property);
                member.Collection?.Bind(false, (_, __) => throw new RpcException(RpcError.InvalidRole, "Client state write."));
                lock (_stateGate) if (_state.TryAdd((target, member.Property), member)) Volatile.Write(ref _stateSnapshot, _state.Values.ToArray());
            }
            Observe(RequestState(target, 0));
        }
        private static IEnumerable<PropertyInfo> StateProperties(Type contract) => new[] { contract }.Concat(contract.GetInterfaces())
            .SelectMany(t => t.GetProperties()).GroupBy(p => p.Name).Select(g => g.First());
        private static StateMember CreateMember(uint target, Type contract, PropertyInfo property)
        {
            var member = new StateMember { Target = target, Property = PropertyId(contract, property.Name), Info = property };
            var type = property.PropertyType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>))
                member.Collection = (INetworkState)Activator.CreateInstance(typeof(NetworkList<>).MakeGenericType(type.GetGenericArguments()))!;
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                member.Collection = (INetworkState)Activator.CreateInstance(typeof(NetworkDictionary<,>).MakeGenericType(type.GetGenericArguments()))!;
            else member.Value = type.IsValueType ? Activator.CreateInstance(type) : null;
            return member;
        }
        private static Action<NetMessageBag> CreateStateWriter(PropertyInfo property, object instance) =>
            (Action<NetMessageBag>)typeof(RpcContextService).GetMethod(nameof(CreateStateWriterTyped), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(property.PropertyType).Invoke(null, new[] { (object)property, instance })!;
        private static Action<NetMessageBag> CreateStateWriterTyped<T>(PropertyInfo property, object instance)
        {
            var getter = (Func<T>)property.GetMethod!.CreateDelegate(typeof(Func<T>), instance);
            return bag => bag.Write(getter());
        }
        public T GetValue<T>(uint targetId, uint propertyId)
        { CheckAlive(); if (!_state.TryGetValue((targetId, propertyId), out var member)) throw new InvalidOperationException("Unknown SyncVar."); T value; lock (_stateGate) value = (T)member.Value!; return NetValue<T>.Copy(value); }
        public NetworkList<T> GetList<T>(uint targetId, uint propertyId) { CheckAlive(); return (NetworkList<T>)_state[(targetId, propertyId)].Collection!; }
        public NetworkDictionary<TKey, TValue> GetDictionary<TKey, TValue>(uint targetId, uint propertyId) where TKey : notnull { CheckAlive(); return (NetworkDictionary<TKey, TValue>)_state[(targetId, propertyId)].Collection!; }
        private void RemoveState(uint target)
        {
            lock (_stateGate)
            {
                foreach (var member in _stateSnapshot)
                    if (member.Target == target && _state.TryRemove((target, member.Property), out _)) member.Collection?.Bind(false, (_, __) => { });
                Volatile.Write(ref _stateSnapshot, _state.Values.ToArray());
            }
        }
        private void PublishOperation(StateMember member, INetworkState collection, NetMessageBag delta)
        {
            using var payload = NetMessageBag.Pool(); payload.Write(collection.Schema); payload.Append(delta);
            // Async sends own their bytes before returning to the collection's mutation path.
            foreach (var peer in Volatile.Read(ref _peerSnapshot)) Observe(SendFrame(peer.Key,
                new NetRpcModel(NetRpcMessageKind.SyncOperation, member.Target, member.Property, 0, payload.Count, payload.Memory, Scope), expected: peer.Value));
        }
        public async UniTask PublishStateAsync(bool forceSnapshot = false)
        {
            await _publishGate.WaitAsync().ConfigureAwait(false);
            try { await PublishStateCore(forceSnapshot); }
            finally { _publishGate.Release(); }
        }
        private async UniTask PublishStateCore(bool forceSnapshot)
        {
            CheckAlive(); if (!IsServer) throw new RpcException(RpcError.InvalidRole, "Only Host publishes state.");
            EnsureServices();
            foreach (var member in Volatile.Read(ref _stateSnapshot))
            {
                if (!_state.TryGetValue((member.Target, member.Property), out var current) || !ReferenceEquals(member, current)) continue;
                if (member.Collection != null) { if (forceSnapshot) await SendMember(member, null); continue; }
                bool changed;
                using (var bag = NetMessageBag.Pool())
                {
                    member.WriteValue(bag);
                    lock (_stateGate)
                    {
                        changed = member.LastValue == null || !bag.Memory.Span.SequenceEqual(member.LastValue);
                        if (changed) { member.LastValue = bag.Memory.ToArray(); member.Revision++; }
                    }
                }
                if (changed || forceSnapshot) await SendMember(member, null);
            }
            await PublishComponents(forceSnapshot);
        }
        private async UniTask SendMember(StateMember member, uint? destination, Connection? origin = null)
        {
            using var bag = NetMessageBag.Pool();
            if (member.Collection != null)
            {
                bag.Write(member.Collection.Schema);
                using var snapshot = member.Collection.Snapshot(); bag.Append(snapshot);
            }
            else
            {
                bag.Write(StateSchema.For(member.Info.PropertyType));
                using var current = NetMessageBag.Pool(); member.WriteValue(current);
                lock (_stateGate)
                {
                    if (member.LastValue == null || !current.Memory.Span.SequenceEqual(member.LastValue)) { member.LastValue = current.Memory.ToArray(); member.Revision++; }
                    bag.Write(member.Revision); bag.Append(current);
                }
            }
            var frame = new NetRpcModel(NetRpcMessageKind.SyncSnapshot, member.Target, member.Property, 0, bag.Count, bag.Memory, Scope);
            if (destination.HasValue) await SendFrame(destination.Value, frame, expected: origin);
            else foreach (var peer in Volatile.Read(ref _peerSnapshot)) await SendFrame(peer.Key, frame, expected: peer.Value);
        }
        private UniTask RequestState(uint target, uint property) => SendFrame(1,
            new NetRpcModel(NetRpcMessageKind.SyncRequest, target, property, 0, 0, ReadOnlyMemory<byte>.Empty, Scope));
        private async UniTask SendRequestedState(uint peer, NetRpcModel model, Connection origin)
        {
            foreach (var member in Volatile.Read(ref _stateSnapshot))
                if (member.Target == model.TargetId && (model.MethodId == 0 || model.MethodId == member.Property))
                    await SendMember(member, peer, origin);
        }
        private UniTask ReceiveState(uint peer, NetRpcModel model, Connection origin)
        {
            if (model.Kind == NetRpcMessageKind.SyncRequest) return IsServer ? SendRequestedState(peer, model, origin) : UniTask.CompletedTask;
            if (IsServer || peer != 1) return UniTask.CompletedTask;
            if (model.Kind == NetRpcMessageKind.Component) { ReceiveComponent(model); return UniTask.CompletedTask; }
            if (!_state.TryGetValue((model.TargetId, model.MethodId), out var state)) return UniTask.CompletedTask;
            using var reader = NetMessageReader.Rent(model.Payload, model.ArgumentCount);
            var schema = reader.Read<ulong>(); var revision = reader.Read<long>();
            if (revision < 0) throw new RpcException(RpcError.InvalidPayload, "Negative state revision.");
            if (schema != (state.Collection?.Schema ?? StateSchema.For(state.Info.PropertyType))) throw new RpcException(RpcError.InvalidPayload, "SyncVar schema mismatch.");
            if (state.Collection != null)
            {
                if (!state.Collection.Apply(reader, model.Kind == NetRpcMessageKind.SyncSnapshot, revision)) return RequestState(model.TargetId, model.MethodId);
            }
            else
            {
                if (model.Kind != NetRpcMessageKind.SyncSnapshot) throw new RpcException(RpcError.InvalidPayload, "Scalar requires snapshot.");
                var value = reader.Read(state.Info.PropertyType); reader.Complete();
                lock (_stateGate) { if (revision <= state.Revision) return UniTask.CompletedTask; state.Value = value; state.Revision = revision; }
                StateChanged?.Invoke(model.TargetId, model.MethodId);
            }
            return UniTask.CompletedTask;
        }
        public void AttachEntities(IEntitiesService entities)
        {
            if (_entities != null) throw new InvalidOperationException("Entity service already attached.");
            _entities = entities; entities.Registered += RegisterEntity; entities.Unregistered += UnregisterEntity;
            foreach (var entity in entities.Entities) RegisterEntity(entity);
        }
        private void RegisterEntity(NetEntity entity)
        {
            var identity = entity.ServiceProvider.GetService<INetworkIdentity>(); if (identity == null) return;
            if (_networkEntities.TryGetValue(identity.EntityId, out var existing) && ReferenceEquals(existing, entity)) return;
            var components = entity.ServiceProvider.GetServices<INetComponent>().ToArray();
            if (identity.EntityId == 0 || components.Any(c => c.ComponentId == 0) || components.Select(c => c.ComponentId).Distinct().Count() != components.Length || !_networkEntities.TryAdd(identity.EntityId, entity))
                throw new InvalidOperationException("Duplicate network entity/component identity.");
            try
            {
                foreach (var component in components)
                {
                    component.SetAuthority(IsServer);
                    lock (_stateGate)
                    {
                        _components[(identity.EntityId, component.ComponentId)] = new ComponentEntry { Entity = entity, Component = component };
                        Volatile.Write(ref _componentSnapshot, _components.ToArray());
                    }
                }
            }
            catch { UnregisterEntity(entity); throw; }
        }
        private void UnregisterEntity(NetEntity entity)
        {
            lock (_stateGate)
            {
                foreach (var item in _componentSnapshot) if (ReferenceEquals(item.Value.Entity, entity)) _components.TryRemove(item.Key, out _);
                Volatile.Write(ref _componentSnapshot, _components.ToArray());
            }
            foreach (var item in _networkEntities.Where(p => ReferenceEquals(p.Value, entity)).ToArray()) _networkEntities.TryRemove(item.Key, out _);
        }
        private async UniTask PublishComponents(bool force)
        {
            foreach (var pair in Volatile.Read(ref _componentSnapshot))
            {
                var entry = pair.Value; var component = entry.Component;
                if (!_components.TryGetValue(pair.Key, out var current) || !ReferenceEquals(entry, current)) continue;
                var fingerprint = component.Fingerprint;
                lock (_stateGate) if (!force && entry.SentRevision >= 0 && fingerprint == entry.LastFingerprint) continue;
                using var value = NetMessageBag.Pool(); var capturedRevision = component.CaptureSnapshot(value);
                var wireFingerprint = StateSchema.Hash(value.Memory.Span); long revision;
                lock (_stateGate)
                {
                    if (entry.SentRevision == long.MaxValue && fingerprint != entry.LastFingerprint) throw new RpcException(RpcError.LimitExceeded, "Component revision exhausted.");
                    revision = entry.SentRevision < 0 || fingerprint != entry.LastFingerprint ? Math.Max(capturedRevision, entry.SentRevision + 1) : entry.SentRevision;
                    entry.SentRevision = revision; entry.LastFingerprint = fingerprint;
                }
                using var bag = NetMessageBag.Pool(); bag.Write(component.SchemaFingerprint); bag.Write(revision); bag.Write(wireFingerprint); bag.Append(value);
                var frame = new NetRpcModel(NetRpcMessageKind.Component, pair.Key.Entity, pair.Key.Component, 0, bag.Count, bag.Memory, Scope);
                foreach (var peer in Volatile.Read(ref _peerSnapshot)) await SendFrame(peer.Key, frame, true, expected: peer.Value);
            }
        }
        private void ReceiveComponent(NetRpcModel model)
        {
            if (!_components.TryGetValue((model.TargetId, model.MethodId), out var entry)) return;
            using var reader = NetMessageReader.Rent(model.Payload, model.ArgumentCount);
            var schema = reader.Read<ulong>(); var revision = reader.Read<long>(); var fingerprint = reader.Read<ulong>();
            if (schema != entry.Component.SchemaFingerprint || revision < 0) throw new RpcException(RpcError.InvalidPayload, "Component schema/revision mismatch.");
            lock (_stateGate) if (revision <= entry.ReceivedRevision) return;
            // Reconstruct only the envelope offset; the snapshot itself remains sequential MessagePack data.
            var bytes = model.Payload; for (int i = 0; i < 3; i++) { int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Span); bytes = bytes.Slice(4 + size); }
            if (StateSchema.Hash(bytes.Span) != fingerprint) throw new RpcException(RpcError.InvalidPayload, "Component value fingerprint mismatch.");
            entry.Component.ApplySnapshot(reader, revision);
            lock (_stateGate) entry.ReceivedRevision = Math.Max(entry.ReceivedRevision, revision);
        }
        private void DisposeState()
        {
            _synchronization?.Cancel();
            _synchronization?.Dispose();
            if (_entities != null) { _entities.Registered -= RegisterEntity; _entities.Unregistered -= UnregisterEntity; }
            lock (_stateGate)
            {
                foreach (var member in _stateSnapshot) member.Collection?.Bind(false, (_, __) => { });
                _components.Clear(); _networkEntities.Clear(); _state.Clear();
                Volatile.Write(ref _stateSnapshot, Array.Empty<StateMember>());
                Volatile.Write(ref _componentSnapshot, Array.Empty<KeyValuePair<(uint Entity, uint Component), ComponentEntry>>());
            }
        }
    }
}
