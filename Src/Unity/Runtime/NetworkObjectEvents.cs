using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace BITKit.Multiplayer.Unity
{
    [AddComponentMenu("BITKit/Networking/Network Object Events")]
    [DefaultExecutionOrder(-2000)]
    public sealed class NetworkObjectEvents : NetworkBehaviour
    {
        [SerializeField] private Behaviour[] hostOnly = Array.Empty<Behaviour>();
        [SerializeField] private bool disableUntilBound = true;
        [SerializeField] private UnityEvent onHostReady = new UnityEvent();
        [SerializeField] private UnityEvent onClientReady = new UnityEvent();
        [SerializeField] private UnityEvent onNetworkDespawn = new UnityEvent();
        private readonly Dictionary<Behaviour, bool> _original = new Dictionary<Behaviour, bool>();
        public UnityEvent HostReady => onHostReady;
        public UnityEvent ClientReady => onClientReady;
        public UnityEvent Despawned => onNetworkDespawn;
        public void ConfigureHostOnly(params Behaviour[] behaviours)
        {
            if (IsBound) throw new InvalidOperationException("Configure role behaviours before binding");
            Restore(); hostOnly = behaviours ?? Array.Empty<Behaviour>();
        }
        private void Awake()
        {
            if (IsSpawned) Apply(IsServer);
            else if (disableUntilBound) Apply(false);
        }
        protected override void OnNetworkBinding() => Apply(false);
        protected override void OnStartServer() { Apply(true); onHostReady.Invoke(); }
        protected override void OnStartClient() { Apply(false); onClientReady.Invoke(); }
        protected override void OnNetworkDespawn() { Restore(); onNetworkDespawn.Invoke(); }
        private void Apply(bool enabledForRole)
        {
            foreach (var behaviour in hostOnly)
            {
                if (!behaviour || ReferenceEquals(behaviour, this)) continue;
                if (!_original.ContainsKey(behaviour)) _original.Add(behaviour, behaviour.enabled);
                behaviour.enabled = enabledForRole;
            }
        }
        private void Restore()
        {
            foreach (var pair in _original) if (pair.Key) pair.Key.enabled = pair.Value;
            _original.Clear();
        }
        protected override void OnDestroy()
        {
            base.OnDestroy();
            Restore(); // also handles destruction before a first successful binding
        }
    }
}
