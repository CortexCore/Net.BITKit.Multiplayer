using Cysharp.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using BITKit.Multiplayer.CodeGen;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcWeaverTests
{
    [Fact]
    public void TypeOptInWeavesBothBackendsInOneAssemblyWithoutRewritingSiblings()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var input = Path.Combine(root, "Artifacts/bin/MixedBackendFixtures/Release/net10.0/MixedBackendFixtures.dll");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters
        { InMemory = true, AssemblyResolver = resolver });
        Assert.Empty(Weaver.WeaveMixedModule(module));
        var legacy = module.Types.Single(type => type.Name == "LegacyActor");
        var current = module.Types.Single(type => type.Name == "NewActor");
        Assert.Contains(legacy.Methods, method => method.Name.StartsWith("__bitkit_recv_"));
        Assert.DoesNotContain(legacy.Methods, method => method.Name.StartsWith("__netrpc_recv_"));
        Assert.Contains(current.Methods, method => method.Name.StartsWith("__netrpc_recv_"));
        Assert.DoesNotContain(current.Methods, method => method.Name.StartsWith("__bitkit_recv_"));
        Assert.Single(module.Assembly.CustomAttributes, attribute =>
            attribute.AttributeType.FullName == "BITKit.Multiplayer.WovenAssemblyAttribute");
    }

    private sealed class Sink : BITKit.Multiplayer.NetRpc.ITransport
    {
        public event Action<ReadOnlyMemory<byte>>? OnReceived { add { } remove { } }
        public int Count;
        public bool Capture;
        public byte[]? Last;
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        { Count++; if (Capture) Last = payload.ToArray(); return UniTask.CompletedTask; }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Send(payload, cancellationToken);
    }
    private sealed class NoopRemoteFactory : IRemoteInterfaceFactory
    {
        public object Create(Type contract, RpcContext context) => throw new NotSupportedException();
        public void RegisterReceiver(Type contract, RpcContextService runtime, uint targetId) { }
    }
    private static (Assembly Assembly, AssemblyLoadContext Loader) Load()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var input = Path.Combine(root, "Artifacts/bin/NetRpcFixtures/Release/net10.0/NetRpcFixtures.dll");
        var output = Path.Combine(root, "Artifacts/NetRpcWoven", Guid.NewGuid().ToString("N"), "NetRpcFixtures.dll");
        Assert.Empty(Weaver.WeaveNetRpc(input, output));
        var loader = new AssemblyLoadContext("netrpc-" + Guid.NewGuid(), isCollectible: true);
        loader.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name)
            ?? (File.Exists(Path.Combine(Path.GetDirectoryName(input)!, name.Name + ".dll")) ? loader.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(input)!, name.Name + ".dll")) : null);
        return (loader.LoadFromAssemblyPath(output), loader);
    }
    [Fact]
    public async Task ActualWovenAssemblyRoutesConcreteAndInterfaceTaskValueTaskUniTaskAndBroadcast()
    {
        var (assembly, loader) = Load();
        try
        {
            var type = assembly.GetType("NetRpcFixtures.Actor")!; var contract = assembly.GetType("NetRpcFixtures.IActor")!;
            using (var module = ModuleDefinition.ReadModule(assembly.Location))
            {
                var actor = module.Types.Single(t => t.Name == "Actor");
                var wrapper = actor.Methods.Single(m => m.Name == "Plus");
                Assert.Contains(wrapper.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "FinishUniTask");
                Assert.DoesNotContain(wrapper.Body.Instructions, i => i.Operand is MethodReference m && (m.Name == "AsTask" || m.Name == "AsUniTask" || m.Name == "FinishTask"));
                var id = RpcContextService.RpcMethodId(type, type.GetMethod("Plus")!);
                var receiver = actor.Methods.Single(m => m.Name == "__netrpc_recv_" + id);
                Assert.Contains(receiver.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "CompleteUniTask");
                Assert.DoesNotContain(receiver.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "AsTask");
            }
            var h = Activator.CreateInstance(type)!; var c = Activator.CreateInstance(type)!; var (a, b) = NetRpcDesignTests.Pair.Create();
            using var provider = new ServiceCollection().BuildServiceProvider();
            using var host = new RpcContextService(provider, b, true); using var client = new RpcContextService(provider, a, false);
            host.RegisterTarget(7, h); client.RegisterTarget(7, c);
            contract.GetMethod("Fire")!.Invoke(c, new object[] { 10 });
            Assert.Equal(10, type.GetProperty("Damage")!.GetValue(h)); Assert.Equal(0, type.GetProperty("Damage")!.GetValue(c));
            Assert.Equal(12, await (Task<int>)type.GetMethod("Read")!.Invoke(c, new object[] { 2 })!);
            Assert.Equal(42, await (UniTask<int>)contract.GetMethod("Plus")!.Invoke(c, new object[] { 20, 22 })!);
            await (UniTask)contract.GetMethod("Wait")!.Invoke(c, null)!;
            Assert.Equal(17, await (ValueTask<int>)contract.GetMethod("Value")!.Invoke(c, new object[] { 17 })!);
            type.GetMethod("Notify")!.Invoke(h, new object[] { 1 }); type.GetMethod("Pose")!.Invoke(h, new object[] { 2 });
            Assert.Equal(3, type.GetProperty("Broadcasts")!.GetValue(h)); Assert.Equal(3, type.GetProperty("Broadcasts")!.GetValue(c)); Assert.Equal(1, b.FastCount);
            Assert.Equal(14, await (Task<int>)type.GetMethod("Nested")!.Invoke(c, new object[] { 3 })!);
            await Assert.ThrowsAsync<RpcException>(() => (Task)type.GetMethod("Fault")!.Invoke(c, null)!);
            var rejected = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Notify")!.Invoke(c, new object[] { 1 })); Assert.IsType<RpcException>(rejected.InnerException);
            var values = new[] { 1 }; await (Task)type.GetMethod("LocalReference")!.Invoke(h, new object[] { values })!;
            Assert.Equal(2, values[0]); // Host local path passes the real arguments to the original body, without serialization.
            type.GetMethod("BroadcastReference")!.Invoke(h, new object[] { values }); Assert.Equal(3, values[0]);
            Assert.Equal(4, type.GetProperty("Broadcasts")!.GetValue(h)); Assert.Equal(4, type.GetProperty("Broadcasts")!.GetValue(c));
            host.Authorize = (_, _, _) => false;
            Assert.IsType<RpcException>(Assert.Throws<TargetInvocationException>(() => type.GetMethod("LocalReference")!.Invoke(h, new object[] { values })).InnerException);
            Assert.Equal(3, values[0]);
        }
        finally { loader.Unload(); }
    }

    [Fact]
    public void WarmWovenTypedVoidSendIsAllocationFreeWithSynchronousTransport()
    {
        var (assembly, loader) = Load();
        try
        {
            var type = assembly.GetType("NetRpcFixtures.Actor")!; var actor = Activator.CreateInstance(type)!; var sink = new Sink();
            using var services = new ServiceCollection().BuildServiceProvider(); using var runtime = new RpcContextService(services, sink, false);
            runtime.RegisterTarget(7, actor);
            var fire = (Action<int>)type.GetMethod("Fire")!.CreateDelegate(typeof(Action<int>), actor);
            for (int i = 0; i < 100; i++) fire(i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) fire(i);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); Assert.Equal(1100, sink.Count);
        }
        finally { loader.Unload(); }
    }

    [Fact]
    public void WovenServiceUsesInjectedContextWithOrdinaryAndContractDiRegistrations()
    {
        var (assembly, loader) = Load();
        try
        {
            var type = assembly.GetType("NetRpcFixtures.Actor")!;
            var contract = assembly.GetType("NetRpcFixtures.IActor")!;
            Assert.Contains(type.GetConstructors(), constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRpcContext<>)));

            var directSink = new Sink();
            var directServices = new ServiceCollection();
            directServices.AddSingleton(type);
            directServices.AddNetRpc(false, _ => directSink);
            var directProvider = directServices.BuildServiceProvider();
            var direct = directProvider.GetRequiredService(type);
            type.GetMethod("Fire")!.Invoke(direct, new object[] { 1 });
            Assert.Equal(1, directSink.Count);
            directProvider.Dispose();
            var disposed = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Fire")!.Invoke(direct, new object[] { 1 }));
            Assert.IsType<ObjectDisposedException>(disposed.InnerException);

            var contractSink = new Sink { Capture = true };
            var contractServices = new ServiceCollection();
            contractServices.AddSingleton<IRemoteInterfaceFactory, NoopRemoteFactory>();
            typeof(NetRpcServiceCollectionExtensions).GetMethods().Single(method => method.Name == "AddNetRpcService" &&
                    method.IsGenericMethodDefinition).MakeGenericMethod(contract, type)
                .Invoke(null, new object[] { contractServices });
            contractServices.AddNetRpc(false, _ => contractSink);
            using var contractProvider = contractServices.BuildServiceProvider();
            var implementation = contractProvider.GetRequiredService(type);
            type.GetMethod("Fire")!.Invoke(implementation, new object[] { 1 });
            Assert.Equal(RpcContextService.ContractId(contract), NetRpcCodec.Decode(contractSink.Last!).TargetId);
        }
        finally { loader.Unload(); }
    }

    [Fact]
    public void ExplicitContextConstructorIsRegisteredOnceAndBusinessDisposeIsPreserved()
    {
        var (assembly, loader) = Load();
        try
        {
            var type = assembly.GetType("NetRpcFixtures.ExplicitContextActor")!;
            Assert.Single(type.GetConstructors());
            var sink = new Sink();
            var services = new ServiceCollection();
            services.AddSingleton(type);
            services.AddNetRpc(false, _ => sink);
            var provider = services.BuildServiceProvider();
            var actor = provider.GetRequiredService(type);
            type.GetMethod("Fire")!.Invoke(actor, new object[] { 1 });
            Assert.Equal(1, sink.Count);
            provider.Dispose();
            Assert.True((bool)type.GetProperty("BusinessDisposed")!.GetValue(actor)!);
        }
        finally { loader.Unload(); }
    }

    [Fact]
    public void ActualUnsupportedDeclarationsFailCodeGenerationBeforeOutputIsWritten()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var input = Path.Combine(root, "Artifacts/bin/NetRpcInvalidFixtures/Release/net10.0/NetRpcInvalidFixtures.dll");
        var output = Path.Combine(root, "Artifacts/NetRpcWoven", Guid.NewGuid().ToString("N"), "Invalid.dll");
        var errors = Weaver.WeaveNetRpc(input, output);
        Assert.Equal(9, errors.Count); Assert.Contains(errors, e => e.Contains("UnreliableResult")); Assert.Contains(errors, e => e.Contains("ByReference"));
        Assert.Contains(errors, e => e.Contains("MissingDisposable") && e.Contains("IDisposable"));
        Assert.Contains(errors, e => e.Contains("MissingContext") && e.Contains("IRpcContext")); Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task WovenImplementationBoundThroughInterfaceUsesRawReceiverAndSameConcreteSemantics()
    {
        var (assembly, loader) = Load();
        try
        {
            var type = assembly.GetType("NetRpcFixtures.Actor")!; var contract = assembly.GetType("NetRpcFixtures.IActor")!;
            var h = Activator.CreateInstance(type)!; var c = Activator.CreateInstance(type)!; var (a, b) = NetRpcDesignTests.Pair.Create();
            using var services = new ServiceCollection().BuildServiceProvider(); using var host = new RpcContextService(services, b, true); using var client = new RpcContextService(services, a, false);
            var target = RpcContextService.ContractId(contract); host.RegisterTarget(target, h, contract); client.RegisterTarget(target, c, contract);
            var readId = RpcContextService.RpcMethodId(contract, contract.GetMethod("Read")!);
            host.SetGeneratedReceiver(target, readId, (_, _) => throw new Exception("A proxy receiver must not replace the woven raw receiver."));
            type.GetMethod("Fire")!.Invoke(c, new object[] { 10 });
            Assert.Equal(12, await (Task<int>)contract.GetMethod("Read")!.Invoke(c, new object[] { 2 })!);
            using var bag = NetMessageBag.Pool(); bag.Write(3);
            Assert.Equal(13, await client.CreateContext(target).Request<int>(new NetRpcModel(NetRpcMessageKind.Call, target, readId, 0, bag.Count, bag.Memory)));
            Assert.Equal(0, type.GetProperty("Damage")!.GetValue(c));
            type.GetMethod("Notify")!.Invoke(h, new object[] { 1 });
            Assert.Equal(1, type.GetProperty("Broadcasts")!.GetValue(c));
            Assert.Equal(16, await (Task<int>)type.GetMethod("Nested")!.Invoke(c, new object[] { 5 })!);
        }
        finally { loader.Unload(); }
    }
}
