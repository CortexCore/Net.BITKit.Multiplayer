using System;
using System.Collections.Concurrent;
using System.Linq;

namespace BITKit.Multiplayer.NetRpc
{
    /// <summary>Uses build-generated native proxy types. There is no Reflection.Emit, Roslyn, or dynamic compilation in this path.</summary>
    public sealed class PrecompiledRemoteInterfaceFactory : IRemoteInterfaceFactory
    {
        private readonly ConcurrentDictionary<Type, Type> _proxies = new();
        private Type Proxy(Type contract) => _proxies.GetOrAdd(contract, type =>
        {
            var name = RemoteInterfaceSourceGenerator.ProxyName(type);
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.GetType(name, false))
                .Where(t => t != null && type.IsAssignableFrom(t)).Distinct().ToArray();
            // Godot's collectible script loader loads compiled DLLs from bytes (Location is empty).
            // Prefer physical build outputs when present; otherwise require one unambiguous loaded proxy.
            var physical = candidates.Where(t => !string.IsNullOrEmpty(t!.Assembly.Location)).ToArray();
            var matches = physical.Length != 0 ? physical : candidates;
            if (matches.Length != 1) throw new InvalidOperationException("Expected one generated proxy for " + type + ". Generate it with CodeGen --remote, or install the optional RemoteCompiler adapter.");
            return matches[0]!;
        });
        public object Create(Type contract, RpcContext context) => Activator.CreateInstance(Proxy(contract), context)!;
        public void RegisterReceiver(Type contract, RpcContextService runtime, uint targetId)
        {
            var proxy = Proxy(contract);
            foreach (var method in RpcContextService.ContractMethods(contract))
            {
                var id = RpcContextService.RpcMethodId(contract, method);
                runtime.SetGeneratedReceiver(targetId, id, (NetRpcReceiver)proxy.GetMethod("__netrpc_recv_" + id)!.CreateDelegate(typeof(NetRpcReceiver)));
            }
        }
    }
}
