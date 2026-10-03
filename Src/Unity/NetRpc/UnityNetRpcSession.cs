using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.NetRpc.Unity
{
    /// <summary>Unity composition owner. Construct/Pump/Dispose on main thread. Socket/listener choice stays outside business services.</summary>
    public sealed class UnityNetRpcSession : IDisposable
    {
        public UnityNetRpcDispatcher Dispatcher { get; } = new UnityNetRpcDispatcher();
        public ServiceProvider Services { get; }
        public RpcContextService Runtime { get; }
        private readonly List<UnityNetRpcTransport> _wires = new List<UnityNetRpcTransport>();
        private readonly List<IAsyncDisposable> _connections = new List<IAsyncDisposable>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly bool _host;
        private bool _disposed, _publishing;
        private double _lastPublish, _lastSnapshot;
        public UniTask Disposal { get; private set; } = UniTask.CompletedTask;
        public event Action<Exception> Faulted;
        public UnityNetRpcSession(IServiceCollection services, bool host, ulong scope)
        {
            _host = host;
            services.TryAddSingleton<IEntitiesService, EntitiesService>();
            services.TryAddSingleton<IRemoteInterfaceFactory, PrecompiledRemoteInterfaceFactory>();
            services.AddSingleton(provider =>
            {
                var runtime = new RpcContextService(provider, host, scope);
                runtime.AttachEntities(provider.GetRequiredService<IEntitiesService>()); return runtime;
            });
            Services = services.BuildServiceProvider(); Runtime = Services.GetRequiredService<RpcContextService>();
            Runtime.Faulted += Report;
        }
        public CancellationToken Lifetime => _lifetime.Token;
        public void AttachPeer(uint peer, ITransport transport, ITransportLifetime life, IAsyncDisposable connectionOwner = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityNetRpcSession));
            if (Thread.CurrentThread.ManagedThreadId != Dispatcher.MainThreadId) throw new InvalidOperationException("Attach Unity NetRpc peers on main thread.");
            var wire = new UnityNetRpcTransport(transport, life, Dispatcher); wire.Faulted += Report;
            try { Runtime.AttachPeer(peer, wire); _wires.Add(wire); if (connectionOwner != null) _connections.Add(connectionOwner); }
            catch { wire.Dispose(); throw; }
        }
        public void Pump(double unscaledSeconds)
        {
            if (_disposed) return; Dispatcher.Pump();
            if (_host && !_publishing && unscaledSeconds - _lastPublish >= 0.05)
            { _lastPublish = unscaledSeconds; Publish(unscaledSeconds).Forget(); }
        }
        private async UniTaskVoid Publish(double seconds)
        {
            _publishing = true;
            try { bool full = seconds - _lastSnapshot >= 1; if (full) _lastSnapshot = seconds; await Runtime.PublishStateAsync(full); }
            catch (Exception e) { if (!_disposed) Report(e); }
            finally { _publishing = false; }
        }
        private void Report(Exception e) => Faulted?.Invoke(e);
        public void Dispose()
        {
            if (_disposed) return;
            if (Thread.CurrentThread.ManagedThreadId != Dispatcher.MainThreadId) throw new InvalidOperationException("Dispose Unity NetRpc session on main thread.");
            _disposed = true; _lifetime.Cancel(); Runtime.Faulted -= Report;
            var wires = _wires.ToArray(); var connections = _connections.ToArray(); _wires.Clear(); _connections.Clear();
            try { Services.Dispose(); }
            finally
            {
                foreach (var wire in wires) { wire.Faulted -= Report; wire.Dispose(); }
                Dispatcher.Dispose(); Disposal = CloseConnections(connections, wires).Preserve();
            }
        }
        private async UniTask CloseConnections(IAsyncDisposable[] connections, UnityNetRpcTransport[] wires)
        {
            var tasks = new List<UniTask>();
            foreach (var owner in connections) { try { tasks.Add(CloseOwner(owner)); } catch (Exception e) { tasks.Add(UniTask.FromException(e)); } }
            foreach (var wire in wires) tasks.Add(wire.OutboundCompletion);
            try { await UniTask.WhenAll(tasks); }
            finally { _lifetime.Dispose(); }
        }
        private static async UniTask CloseOwner(IAsyncDisposable owner) => await owner.DisposeAsync().ConfigureAwait(false);
    }
}
