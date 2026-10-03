using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using BITKit.Multiplayer.NetRpc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.RemoteCompiler
{
    /// <summary>Optional JIT adapter; Core itself has no Roslyn dependency. Factories contain metadata, never runtime routing.</summary>
    public sealed class RemoteInterfaceCompiler : IRemoteInterfaceFactory
    {
        private readonly ConcurrentDictionary<Type, Lazy<Type>> _types = new();
        private Type Proxy(Type contract) => _types.GetOrAdd(contract, t => new Lazy<Type>(() => Compile(t))).Value;
        public object Create(Type contract, RpcContext context) => Activator.CreateInstance(Proxy(contract), context)!;
        public void RegisterReceiver(Type contract, RpcContextService runtime, uint targetId)
        {
            var proxy = Proxy(contract);
            foreach (var method in contract.GetMethods().Concat(contract.GetInterfaces().SelectMany(t => t.GetMethods())).Where(m => !m.IsSpecialName))
            {
                var id = RpcContextService.RpcMethodId(contract, method);
                runtime.SetGeneratedReceiver(targetId, id, (NetRpcReceiver)proxy.GetMethod("__netrpc_recv_" + id)!.CreateDelegate(typeof(NetRpcReceiver)));
            }
        }
        public static Type Compile(Type contract)
        {
            var source = RemoteInterfaceSourceGenerator.Generate(contract);
            var platform = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            var references = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location).Concat(platform).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("NetRemote_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var stream = new MemoryStream(); var emitted = compilation.Emit(stream);
            if (!emitted.Success) throw new InvalidOperationException("Remote contract compilation failed:\n" + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray()).GetType(RemoteInterfaceSourceGenerator.ProxyName(contract), true)!;
        }
    }
    public static class RemoteCompilerServiceExtensions
    {
        public static IServiceCollection AddGeneratedRemoteInterfaces(this IServiceCollection services)
        { services.AddSingleton<IRemoteInterfaceFactory, RemoteInterfaceCompiler>(); return services; }
    }
}
