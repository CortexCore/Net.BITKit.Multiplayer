using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BITKit.Multiplayer.NetRpc
{
    public interface IRpcContext : IDisposable
    {
        RpcContext Context { get; }
        RpcContext Register(object instance);
        NetRpcInvocation Begin(object instance, uint method, SendTo route, RpcDelivery delivery);
    }

    public interface IRpcContext<TContract> : IRpcContext where TContract : class
    {
        RpcContext Register(TContract instance);
    }

    /// <summary>A DI-scope-owned registration lease for one woven RPC service.</summary>
    public sealed class RpcContext<TContract> : IRpcContext<TContract> where TContract : class
    {
        private readonly RpcContextService _runtime;
        private readonly Type _contract;
        private readonly uint _target;
        private object? _instance;
        private RpcContext? _context;
        private bool _disposed;

        public RpcContext(RpcContextService runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _contract = typeof(TContract);
            _target = RpcContextService.ContractId(_contract);
        }

        public RpcContext Context
        {
            get
            {
                lock (this)
                    return _context ?? throw new InvalidOperationException("RPC context has not registered its service instance.");
            }
        }

        public RpcContext Register(TContract instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (this)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RpcContext<TContract>));
                if (_instance != null && !ReferenceEquals(_instance, instance))
                    throw new InvalidOperationException("An injected RPC context can register only one service instance.");
                _instance = instance;
                return _context ??= _runtime.RegisterTarget(_target, instance, _contract);
            }
        }

        RpcContext IRpcContext.Register(object instance)
        {
            if (instance is not TContract target)
                throw new ArgumentException("RPC service does not implement the context contract.", nameof(instance));
            return Register(target);
        }

        public NetRpcInvocation Begin(object instance, uint method, SendTo route, RpcDelivery delivery)
        {
            lock (this)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RpcContext<TContract>));
                if (_context == null || !ReferenceEquals(_instance, instance))
                    throw new RpcException(RpcError.MissingTarget, "RPC context has not registered this service instance.");
                _runtime.CheckAlive();
                return NetRpcInvocation.Rent(_context, method, route, delivery);
            }
        }

        public void Dispose()
        {
            object? instance;
            lock (this)
            {
                if (_disposed) return;
                _disposed = true;
                instance = _instance;
                _instance = null;
                _context = null;
            }
            if (instance != null) _runtime.RemoveTarget(_target, instance);
        }
    }

    public interface IRemoteInterfaceFactory
    {
        object Create(Type contract, RpcContext context);
        void RegisterReceiver(Type contract, RpcContextService runtime, uint targetId);
    }

    public sealed class NetRpcServiceRegistration
    {
        public NetRpcServiceRegistration(Type contract, Action<IServiceProvider, RpcContextService> register) { Contract = contract; Register = register; }
        public Type Contract { get; }
        internal Action<IServiceProvider, RpcContextService> Register { get; }
        internal static Type? FindContract(IServiceProvider provider, string name)
        {
            var registered = provider.GetServices<NetRpcServiceRegistration>().FirstOrDefault(r => r.Contract.FullName == name);
            if (registered != null) return registered.Contract;
            // Compatibility for the original v1 direct-DI API: search already loaded local metadata only.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null && provider.GetService(type) != null) return type;
            }
            return null;
        }
    }

    public static class NetRpcServiceCollectionExtensions
    {
        public static IServiceCollection AddNetRpcRuntime(this IServiceCollection services, bool isServer, ulong scope = 0)
        {
            AddInfrastructure(services);
            services.AddSingleton(provider =>
            {
                var runtime = new RpcContextService(provider, isServer, scope);
                runtime.AttachEntities(provider.GetRequiredService<IEntitiesService>());
                foreach (var registration in provider.GetServices<NetRpcServiceRegistration>())
                    runtime.AllowContract(registration.Contract);
                return runtime;
            });
            return services;
        }

        public static IServiceCollection AddNetRpc(this IServiceCollection services, bool isServer,
            Func<IServiceProvider, ITransport> transport, ulong scope = 0, NetRpcOptions? options = null)
        {
            AddInfrastructure(services);
            services.AddSingleton(provider =>
            {
                var runtime = new RpcContextService(provider, isServer, scope);
                runtime.AttachPeer(isServer ? 2u : 1u, transport(provider));
                runtime.AttachEntities(provider.GetRequiredService<IEntitiesService>());
                foreach (var registration in provider.GetServices<NetRpcServiceRegistration>())
                    runtime.AllowContract(registration.Contract);
                if (isServer) runtime.StartSynchronization(options ?? new NetRpcOptions());
                return runtime;
            });
            return services;
        }
        private static void AddInfrastructure(IServiceCollection services)
        {
            services.TryAddSingleton<IEntitiesService, EntitiesService>();
            services.TryAddSingleton<IRemoteInterfaceFactory, PrecompiledRemoteInterfaceFactory>();
            services.TryAddTransient(typeof(IRpcContext<>), typeof(RpcContext<>));
        }
        public static IServiceCollection AddRemoteInterface<T>(this IServiceCollection services) where T : class
        {
            services.AddSingleton<T>(provider =>
            {
                var runtime = provider.GetRequiredService<RpcContextService>();
                if (runtime.IsServer) throw new RpcException(RpcError.InvalidRole, "Remote proxy requires Client role.");
                var id = RpcContextService.ContractId(typeof(T));
                runtime.AllowContract(typeof(T));
                runtime.RegisterRemoteState(id, typeof(T));
                return (T)provider.GetRequiredService<IRemoteInterfaceFactory>().Create(typeof(T), runtime.CreateContext(id));
            });
            return services;
        }
        public static IServiceCollection AddNetRpcService<TContract, TImplementation>(this IServiceCollection services)
            where TContract : class where TImplementation : class, TContract
        {
            services.AddSingleton<TImplementation>(provider =>
            {
                var instance = ActivatorUtilities.CreateInstance<TImplementation>(provider);
                var runtime = provider.GetRequiredService<RpcContextService>();
                var id = RpcContextService.ContractId(typeof(TContract));
                runtime.RegisterTarget(id, instance, typeof(TContract));
                if (typeof(TContract).IsInterface) provider.GetService<IRemoteInterfaceFactory>()?.RegisterReceiver(typeof(TContract), runtime, id);
                return instance;
            });
            if (typeof(TContract) != typeof(TImplementation)) services.AddSingleton<TContract>(p => p.GetRequiredService<TImplementation>());
            services.AddSingleton(new NetRpcServiceRegistration(typeof(TContract), (p, runtime) =>
            {
                _ = p.GetRequiredService<TImplementation>();
            }));
            return services;
        }
    }

    /// <summary>Only explicit per-instance bindings; never a global active runtime or ID routing table.</summary>
    public static class NetRpcDispatch
    {
        private sealed class Binding { public RpcContext Context = null!; }
        private static readonly ConditionalWeakTable<object, Binding> Bindings = new();
        internal static void Attach(object instance, RpcContextService runtime, uint target)
        {
            lock (Bindings)
            {
                if (Bindings.TryGetValue(instance, out var old) && (!ReferenceEquals(old.Context.Owner, runtime) || old.Context.TargetId != target))
                    throw new InvalidOperationException("RPC object already belongs to another scope.");
                Bindings.Remove(instance); Bindings.Add(instance, new Binding { Context = runtime.CreateContext(target) });
            }
        }
        internal static void Detach(object instance) { lock (Bindings) Bindings.Remove(instance); }
        public static NetRpcInvocation Begin(object instance, IRpcContext? context, uint method, SendTo route, RpcDelivery delivery) =>
            context != null ? context.Begin(instance, method, route, delivery) : Begin(instance, method, route, delivery);
        public static NetRpcInvocation Begin(object instance, uint method, SendTo route, RpcDelivery delivery)
        {
            if (!Bindings.TryGetValue(instance, out var binding)) throw new RpcException(RpcError.MissingTarget, "RPC object has not been resolved in a network scope.");
            binding.Context.Owner.CheckAlive(); return NetRpcInvocation.Rent(binding.Context, method, route, delivery);
        }
    }

    public sealed class NetRpcInvocation : IDisposable
    {
        [ThreadStatic] private static Stack<NetRpcInvocation>? _pool;
        private RpcContext? _context;
        private uint _method;
        private SendTo _route;
        private RpcDelivery _delivery;
        private NetMessageBag? _bag;
        private bool _disposed;
        private bool _localEntered;
        private bool _localExecuted;
        private NetRpcCallContext? _prior;
        internal static NetRpcInvocation Rent(RpcContext context, uint method, SendTo route, RpcDelivery delivery)
        {
            var invocation = _pool != null && _pool.Count != 0 ? _pool.Pop() : new NetRpcInvocation();
            invocation._context = context; invocation._method = method; invocation._route = route; invocation._delivery = delivery;
            invocation._bag = NetMessageBag.Pool(); invocation._disposed = false; invocation._localExecuted = false; return invocation;
        }
        public bool ShouldExecuteLocal() => _context!.IsServer && _route == SendTo.Host;
        public void EnterLocal()
        {
            if (_localEntered) throw new InvalidOperationException("Local RPC context already entered.");
            _context!.Owner.ValidateLocal(_context.TargetId, _method);
            _prior = NetRpcCallContext.Set(new NetRpcCallContext(1, _context.TargetId)); _localEntered = true;
        }
        public void ExitLocal()
        { if (_localEntered) { NetRpcCallContext.Set(_prior); _prior = null; _localEntered = false; _localExecuted = true; } }
        public void Write<T>(T value) => (_bag ?? throw new ObjectDisposedException(nameof(NetRpcInvocation))).Write(value);
        public void FinishVoid()
        {
            var bag = _bag!; _bag = null;
            _context!.Owner.InvokeWovenVoid(ModelWith(bag), _route, _delivery, bag, _localExecuted);
        }
        private NetRpcModel ModelWith(NetMessageBag bag) => new NetRpcModel(NetRpcMessageKind.Call, _context!.TargetId, _method, 0, bag.Count, bag.Memory, _context.Owner.Scope);
        public Task FinishTask() { var bag = _bag!; _bag = null; return Finish(_context!, ModelWith(bag), bag); }
        private static Task Finish(RpcContext context, NetRpcModel model, NetMessageBag bag) { using (bag) return context.RequestTask(model); }
        public Task<T> FinishTask<T>() { var bag = _bag!; _bag = null; return Finish<T>(_context!, ModelWith(bag), bag); }
        private static Task<T> Finish<T>(RpcContext context, NetRpcModel model, NetMessageBag bag) { using (bag) return context.RequestTask<T>(model); }
        public UniTask FinishUniTask()
        { var bag = _bag!; _bag = null; using (bag) return _context!.Request(ModelWith(bag)); }
        public UniTask<T> FinishUniTask<T>()
        { var bag = _bag!; _bag = null; using (bag) return _context!.Request<T>(ModelWith(bag)); }
        public ValueTask FinishValueTask()
        { var bag = _bag!; _bag = null; using (bag) return _context!.RequestValue(ModelWith(bag)); }
        public ValueTask<T> FinishValueTask<T>()
        { var bag = _bag!; _bag = null; using (bag) return _context!.RequestValue<T>(ModelWith(bag)); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; ExitLocal(); _bag?.Dispose(); _bag = null; _context = null;
            var pool = _pool ??= new Stack<NetRpcInvocation>(); if (pool.Count < 32) pool.Push(this);
        }
    }

    public sealed partial class RpcContextService
    {
        internal void ValidateLocal(uint target, uint method)
        {
            CheckAlive();
            if (!IsServer) throw new RpcException(RpcError.InvalidRole, "Only Host may enter a local authoritative RPC body.");
            if (!_targets.TryGetValue(target, out var descriptor) || !descriptor.Methods.ContainsKey(method)) throw new RpcException(RpcError.MissingMethod, "Local RPC target/method is no longer registered.");
            if (Authorize != null && !Authorize(1, target, method)) throw new RpcException(RpcError.Unauthorized, "Local Host RPC denied.");
        }
        private readonly object _registrationGate = new();
        private bool _servicesInitialized;
        private void EnsureServices()
        {
            lock (_registrationGate)
            {
                if (_servicesInitialized) return;
                foreach (var registration in _services.GetServices<NetRpcServiceRegistration>()) registration.Register(_services, this);
                _servicesInitialized = true;
            }
        }
        internal void InvokeWovenVoid(NetRpcModel call, SendTo route, RpcDelivery delivery, NetMessageBag bag, bool localExecuted = false)
        {
            if (!IsServer && route == SendTo.All) { bag.Dispose(); throw new RpcException(RpcError.InvalidRole, "Only Host may initiate All."); }
            if (IsServer && route == SendTo.All && localExecuted) { Observe(FinishSend(Fanout(call, delivery), bag)); return; }
            if (IsServer)
            {
                try
                {
                    // The synchronous prefix includes the raw void body; local exceptions are observable by its caller.
                    var task = Execute(1, call, false);
                    if (task.Status != UniTaskStatus.Pending) { task.GetAwaiter().GetResult(); bag.Dispose(); }
                    else Observe(FinishSend(task.AsUniTask(), bag));
                }
                catch { bag.Dispose(); throw; }
            }
            else
            {
                if (delivery == RpcDelivery.Unreliable) call = new NetRpcModel(NetRpcMessageKind.FastCall, call.TargetId, call.MethodId, 0, call.ArgumentCount, call.Payload, Scope);
                Observe(FinishSend(SendFrame(1, call, delivery == RpcDelivery.Unreliable), bag));
            }
        }
        private async UniTask Fanout(NetRpcModel call, RpcDelivery delivery)
        { foreach (var peer in Volatile.Read(ref _peerSnapshot)) await SendFrame(peer.Key, call, delivery == RpcDelivery.Unreliable, expected: peer.Value); }
        private static async UniTask FinishSend(UniTask send, NetMessageBag bag) { using (bag) await send; }
    }
}
