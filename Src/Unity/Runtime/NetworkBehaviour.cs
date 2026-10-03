using System;
using System.Threading;
using UnityEngine;

namespace BITKit.Multiplayer.Unity
{
    /// <summary>Unity lifecycle adapter over one existing room binding. No transport, ticker or global runtime.</summary>
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        private RpcRuntime _runtime;
        private TargetKey _target;
        private long _networkId;
        private int _generation, _thread;
        private bool _committed, _spawned, _stopping;
        private SynchronizationContext _context;
        private CancellationTokenRegistration _shutdown;
        private Action<TargetKey> _synchronized, _unbound;
        private Action<long> _members;
        private INetworkOwnership _ownership;
        private Action<PeerId?, PeerId?> _ownershipChanged;
        private PeerId? _ownerPeerId, _notifiedOwner;

        public bool IsBound => _runtime != null;
        public bool IsSpawned => _spawned && _runtime != null && _runtime.IsConnected && _runtime.IsReady;
        public bool IsServer => _runtime != null && _runtime.IsHost;
        public bool IsHost => IsServer;
        public bool IsClient => _runtime != null && _runtime.IsClient;
        public PeerId? OwnerPeerId => _ownerPeerId;
        public bool IsOwner => IsSpawned && _ownerPeerId.HasValue && _ownerPeerId.Value.Equals(_runtime.LocalPeerId);
        public long NetworkId => _networkId;
        public TargetKey NetworkTarget => _target;
        public NetworkTime NetworkTime => _runtime?.NetworkTime;
        protected RpcRuntime Runtime => _runtime;

        /// <summary>Called by a Unity adapter on its main thread. The wire must dispatch state/RPC application on that thread.</summary>
        public IDisposable BindNetwork(RpcRuntime runtime, TargetKey target, long networkId,
            Func<AuthorizationRequest, bool> authorize = null, PeerId? owner = null, INetworkOwnership ownership = null)
        {
            if (!this) throw new ObjectDisposedException(GetType().Name);
            if (_runtime != null) throw new InvalidOperationException("NetworkBehaviour already bound");
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            if (runtime.Role == NetworkRole.Offline || networkId <= 0) throw new ArgumentException("An active network role and positive instance ID are required");
            if (runtime.LifetimeCancellation.IsCancellationRequested) throw new ObjectDisposedException(nameof(runtime));
            _runtime = runtime; _target = target; _networkId = networkId;
            _thread = Thread.CurrentThread.ManagedThreadId; _context = SynchronizationContext.Current;
            _committed = _spawned = false;
            _ownership = ownership; _ownerPeerId = ownership != null ? ownership.OwnerPeerId : owner; _notifiedOwner = null;
            int generation = ++_generation;
            _ownershipChanged = (_, after) => OnMain(generation, () =>
            { _ownerPeerId = after; if (_spawned) NotifyOwnership(); });
            if (_ownership != null) _ownership.OwnershipChanged += _ownershipChanged;
            _synchronized = key => { if (key.Equals(target)) OnMain(generation, TryStart); };
            _unbound = key => { if (key.Equals(target)) OnMain(generation, () => StopBinding(false)); };
            _members = _ => OnMain(generation, () =>
            { if (!runtime.IsConnected || _spawned && !runtime.IsReady) StopBinding(true); else TryStart(); });
            runtime.Synchronized += _synchronized; runtime.TargetUnbound += _unbound; runtime.MembersChanged += _members;
            try
            {
                OnNetworkBinding();
                if (_generation != generation || _runtime != runtime) throw new OperationCanceledException("Binding canceled by initialization");
                runtime.Bind(target, this, authorize, owner);
                _committed = true;
                _shutdown = runtime.LifetimeCancellation.Register(() => OnMain(generation, () => StopBinding(false)));
                TryStart();
                if (_generation != generation || _runtime != runtime) throw new OperationCanceledException("Binding canceled by startup callback");
                return new Lease(this, generation);
            }
            catch { if (_generation == generation && _runtime == runtime) StopBinding(_committed); throw; }
        }
        private void OnMain(int generation, Action action)
        {
            if (generation != _generation || _runtime == null) return;
            if (Thread.CurrentThread.ManagedThreadId == _thread) { action(); return; }
            if (_context == null) throw new InvalidOperationException("NetworkBehaviour requires a main-thread-dispatched wire");
            _context.Post(_ => { if (this && generation == _generation && _runtime != null) action(); }, null);
        }
        private void TryStart()
        {
            if (!_committed || _spawned || _runtime == null || !this || !_runtime.IsReady || !_runtime.IsStateSynchronized(_target)) return;
            _spawned = true;
            int generation = _generation;
            try { if (_runtime.IsHost) OnStartServer(); else OnStartClient(); }
            catch (Exception ex) { Debug.LogException(ex, this); }
            if (generation == _generation && _spawned) NotifyOwnership();
        }
        private void NotifyOwnership()
        {
            if (Nullable.Equals(_notifiedOwner, _ownerPeerId)) return;
            var before = _notifiedOwner; _notifiedOwner = _ownerPeerId;
            try { OnOwnershipChanged(before, _ownerPeerId); }
            catch (Exception ex) { Debug.LogException(ex, this); }
        }
        private void StopBinding(bool unbind)
        {
            if (_runtime == null || _stopping) return;
            _stopping = true;
            var runtime = _runtime; var target = _target;
            bool committed = _committed;
            ++_generation; _spawned = false; _committed = false;
            runtime.Synchronized -= _synchronized; runtime.TargetUnbound -= _unbound; runtime.MembersChanged -= _members;
            if (_ownership != null) _ownership.OwnershipChanged -= _ownershipChanged;
            _shutdown.Dispose();
            try
            {
                if (unbind && committed) runtime.Unbind(target);
                _ownerPeerId = null; NotifyOwnership();
                // Also cleans up a binding canceled while it awaited its initial state.
                try { OnNetworkDespawn(); } catch (Exception ex) { Debug.LogException(ex, this); }
            }
            finally
            {
                _runtime = null; _networkId = 0; _target = default;
                _synchronized = _unbound = null; _members = null; _context = null; _stopping = false;
                _ownership = null; _ownershipChanged = null; _ownerPeerId = _notifiedOwner = null;
            }
        }
        public void UnbindNetwork() => StopBinding(true);
        protected virtual void OnNetworkBinding() { }
        protected virtual void OnStartServer() { }
        protected virtual void OnStartClient() { }
        /// <summary>After startup, on authoritative changes, and on release (new owner null).
        /// IsOwner is already updated; a pending, never-started binding does not acquire ownership.</summary>
        protected virtual void OnOwnershipChanged(PeerId? previousOwner, PeerId? newOwner) { }
        protected virtual void OnNetworkDespawn() { }
        protected virtual void OnDestroy() => StopBinding(true);
        // Disabling the behaviour does not unbind it; network lifecycle is independent of Update.
        private sealed class Lease : IDisposable
        {
            private NetworkBehaviour _owner;
            private readonly int _generation;
            internal Lease(NetworkBehaviour owner, int generation) { _owner = owner; _generation = generation; }
            public void Dispose()
            {
                var owner = _owner; _owner = null;
                if (!ReferenceEquals(owner, null) && owner._generation == _generation) owner.StopBinding(true);
            }
        }
    }
}
