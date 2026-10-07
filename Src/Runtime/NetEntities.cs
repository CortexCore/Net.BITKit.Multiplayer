using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.NetRpc
{
    public interface INetworkIdentity { uint EntityId { get; } }
    public sealed class NetworkIdentity : INetworkIdentity
    {
        public NetworkIdentity(uint entityId) { if (entityId == 0) throw new ArgumentOutOfRangeException(nameof(entityId)); EntityId = entityId; }
        public uint EntityId { get; }
    }
    public interface INetComponent
    {
        uint ComponentId { get; }
        ulong SchemaFingerprint { get; }
        ulong Fingerprint { get; }
        long Revision { get; }
        void SetAuthority(bool authority);
        void WriteSnapshot(NetMessageBag bag);
        long CaptureSnapshot(NetMessageBag bag);
        void ApplySnapshot(NetMessageReader reader, long revision);
    }
    public class NetComponent<T> : INetComponent
    {
        private T _value;
        private bool _authority = true;
        private bool _initialized;
        private readonly object _gate = new();
        public NetComponent(uint componentId, T initialValue = default!) { if (componentId == 0) throw new ArgumentOutOfRangeException(nameof(componentId)); ComponentId = componentId; _value = initialValue; }
        public uint ComponentId { get; }
        public ulong SchemaFingerprint => StateSchema.For(typeof(T));
        public long Revision { get; private set; }
        public ulong Fingerprint { get { using var bag = NetMessageBag.Pool(); WriteSnapshot(bag); return StateSchema.Hash(bag.Memory.Span); } }
        public event Action<T, T>? Changed;
        public T Value
        {
            get { lock (_gate) return _authority ? _value : NetValue<T>.Copy(_value); }
            set
            {
                T previous;
                lock (_gate)
                {
                    if (!_authority) throw new RpcException(RpcError.InvalidRole, "Client component writes require a Host RPC.");
                    if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                    if (Revision == long.MaxValue) throw new RpcException(RpcError.LimitExceeded, "Component revision exhausted.");
                    previous = _value; _value = value; Revision++;
                }
                Changed?.Invoke(previous, value);
            }
        }
        public void SetAuthority(bool authority)
        {
            lock (_gate)
            {
                _authority = authority;
                // Local initialization is not a received Host revision. Each Client
                // registration starts a fresh receive baseline, including a reused
                // component, while preserving its displayed value until the snapshot.
                if (!authority) { Revision = 0; _initialized = false; }
            }
        }
        public void WriteSnapshot(NetMessageBag bag) { lock (_gate) bag.Write(_value); }
        public long CaptureSnapshot(NetMessageBag bag) { lock (_gate) { bag.Write(_value); return Revision; } }
        public void ApplySnapshot(NetMessageReader reader, long revision)
        {
            var value = reader.Read<T>(); reader.Complete(); T previous;
            lock (_gate) { if (revision < Revision || revision == Revision && _initialized) return; previous = _value; _value = value; Revision = revision; _initialized = true; }
            Changed?.Invoke(NetValue<T>.Copy(previous), NetValue<T>.Copy(value));
        }
    }
    public sealed class NetEntity
    {
        public NetEntity(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        public IServiceProvider ServiceProvider { get; }
    }
    public interface IEntitiesService
    {
        event Action<NetEntity>? Registered;
        event Action<NetEntity>? Unregistered;
        IReadOnlyList<NetEntity> Entities { get; }
        void Register(NetEntity entity);
        bool Unregister(NetEntity entity);
    }
    public sealed class EntitiesService : IEntitiesService
    {
        private readonly List<NetEntity> _entities = new();
        public event Action<NetEntity>? Registered;
        public event Action<NetEntity>? Unregistered;
        public IReadOnlyList<NetEntity> Entities { get { lock (_entities) return _entities.ToArray(); } }
        public void Register(NetEntity entity)
        {
            lock (_entities) { if (_entities.Contains(entity)) throw new InvalidOperationException("Entity already registered."); _entities.Add(entity); }
            try { Registered?.Invoke(entity); } catch { lock (_entities) _entities.Remove(entity); throw; }
        }
        public bool Unregister(NetEntity entity)
        { lock (_entities) if (!_entities.Remove(entity)) return false; Unregistered?.Invoke(entity); return true; }
    }
}
