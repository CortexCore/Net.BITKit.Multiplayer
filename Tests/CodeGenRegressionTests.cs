using System.Reflection;
using BITKit.Multiplayer;
using BITKit.Multiplayer.CodeGen;
using Fixture;
using Mono.Cecil;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public class CodeGenRegressionTests
{
    [Fact]
    public void PortableSymbolsCoverAllMethodsIncludingTrailingInterfaces()
    {
        using var module = ModuleDefinition.CreateModule("PdbCoverage", ModuleKind.Dll);
        var type = new TypeDefinition("Fixture", "Test", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        var body = new MethodDefinition("Throws", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
        body.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ldnull);
        body.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Throw); type.Methods.Add(body);
        var contract = new TypeDefinition("Fixture", "ITrailing", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface | Mono.Cecil.TypeAttributes.Abstract);
        contract.Methods.Add(new MethodDefinition("NoBody", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Abstract | Mono.Cecil.MethodAttributes.Virtual, module.TypeSystem.Void));
        module.Types.Add(contract);
        Weaver.EnsureSafePortableSymbols(module);
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        module.Write(pe, new WriterParameters { WriteSymbols = true, SymbolStream = pdb, SymbolWriterProvider = new Mono.Cecil.Cil.PortablePdbWriterProvider() });
        pe.Position = 0; pdb.Position = 0;
        using var image = new System.Reflection.PortableExecutable.PEReader(pe);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(image);
        using var symbols = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(pdb);
        Assert.Equal(metadata.MethodDefinitions.Count, symbols.GetMetadataReader().MethodDebugInformation.Count);
        Assert.True(symbols.GetMetadataReader().Documents.Count > 0);
    }
    private sealed class Wire : IRoomWire
    {
        public Wire? Other;
        public PeerId Identity;
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft { add { } remove { } }
        public Task SendAsync(PeerId peer, byte[] data)
        {
            Other!.Received?.Invoke(Identity, data);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
    private static Type MappedType => RuntimeTests.WovenAssembly.GetType("Fixture.Mapped")!;
    private static object NewMapped() => Activator.CreateInstance(MappedType)!;
    private static async Task<int> Invoke(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethods().Single(m => m.Name == name && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args.Select(a => a.GetType())));
        try { return await (Task<int>)method.Invoke(instance, args)!; }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }
    [Fact]
    public async Task Canonical_and_all_interface_aliases_overloads_explicit_and_inherited()
    {
        var hw = new Wire { Identity = new PeerId("host") }; var cw = new Wire { Identity = new PeerId("a") };
        hw.Other = cw; cw.Other = hw;
        using var host = new RpcRuntime(NetworkRole.Host, "mapping", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "mapping", new PeerId("a"), new PeerId("host"), cw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("mapped"); var authority = NewMapped(); var caller = NewMapped();
        MappedType.GetProperty("Version")!.SetValue(caller, 99); // local initialization before Client bind
        host.Bind(key, authority, _ => true); client.Bind(key, caller);
        Assert.Equal(3, (int)MappedType.GetProperty("Version")!.GetValue(authority)!);
        Assert.Equal(3, (int)MappedType.GetProperty("Version")!.GetValue(caller)!);
        Assert.Equal(2, await Invoke(caller, "Ping", 1));
        Assert.Equal(4, await Invoke(caller, "Ping", "four"));
        Assert.Equal(5, await (Task<int>)typeof(ILeft).GetMethod("Ping")!.Invoke(caller, new object[] { 4 })!);
        Assert.Equal(6, await (Task<int>)typeof(IRight).GetMethod("Ping")!.Invoke(caller, new object[] { 5 })!);
        Assert.Equal(9, await (Task<int>)typeof(IBase).GetMethod("Base")!.Invoke(caller, new object[] { 7 })!);
        Assert.Equal(11, await (Task<int>)typeof(IExplicit).GetMethod("Special")!.Invoke(caller, new object[] { 8 })!);
        var routes = new Dictionary<string, SendTo>
        {
            [RpcRuntime.MethodId(typeof(ILeft).GetMethod("Ping")!)] = SendTo.Host,
            [RpcRuntime.MethodId(typeof(IRight).GetMethod("Ping")!)] = SendTo.Host,
            [RpcRuntime.MethodId(typeof(IBase).GetMethod("Base")!)] = SendTo.Host,
            [RpcRuntime.MethodId(typeof(IExplicit).GetMethod("Special")!)] = SendTo.Host
        };
        routes[RpcRuntime.MethodId(typeof(IGeneric<int>).GetMethod("Echo")!)] = SendTo.Host;
        Assert.Equal(13, await client.CreateProxy<IRight>(key, routes).Ping(12));
        Assert.Equal(15, await client.CreateProxy<IChild>(key, routes).Base(13));
        Assert.Equal(18, await client.CreateProxy<IExplicit>(key, routes).Special(15));
        Assert.Equal(19, await client.CreateProxy<IGeneric<int>>(key, routes).Echo(19));
        Assert.Equal(10, (int)MappedType.GetField("Calls")!.GetValue(authority)!);
        Assert.Equal(0, (int)MappedType.GetField("Calls")!.GetValue(caller)!);
        Assert.Equal(RpcError.InvalidRole, Assert.IsType<RpcException>(Assert.Throws<TargetInvocationException>(() => MappedType.GetProperty("Version")!.SetValue(caller, 12)).InnerException).Error);
        using var secondWire = new Wire { Identity = new PeerId("host") };
        using var otherScope = new RpcRuntime(NetworkRole.Host, "other-provider", new PeerId("host"), new PeerId("host"), secondWire);
        Assert.Throws<InvalidOperationException>(() => otherScope.Bind(key, authority, _ => true));
    }
    [Fact]
    public async Task Async_nested_branch_exception_handlers_and_context_restoration()
    {
        var hw = new Wire { Identity = new PeerId("host") }; var cw = new Wire { Identity = new PeerId("a") };
        hw.Other = cw; cw.Other = hw;
        using var host = new RpcRuntime(NetworkRole.Host, "async-regression", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "async-regression", new PeerId("a"), new PeerId("host"), cw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("async"); var authority = NewMapped(); var caller = NewMapped();
        host.Bind(key, authority, _ => true); client.Bind(key, caller);
        Assert.Equal(14, await Invoke(caller, "Nested", 3));
        Assert.Equal("a", MappedType.GetField("SenderBefore")!.GetValue(authority));
        Assert.Equal("a", MappedType.GetField("SenderAfter")!.GetValue(authority));
        Assert.Equal(-1, await Invoke(caller, "Nested", -1));
        Assert.Equal(2, MappedType.GetField("FinallyCount")!.GetValue(authority));
        var fault = await Assert.ThrowsAsync<RpcException>(() => Invoke(caller, "ThrowAsync"));
        Assert.Equal(RpcError.RemoteFault, fault.Error);
        Assert.Null(RpcCallContext.Current);
        Assert.Equal(2, await Invoke(caller, "Ping", 1));
        client.Unbind(key);
        Assert.Equal(RpcError.Disposed, Assert.Throws<TargetInvocationException>(() => MappedType.GetProperty("Version")!.SetValue(caller, 4)).InnerException is RpcException e ? e.Error : throw new Exception("Missing failure"));
        Assert.Equal(RpcError.Disposed, Assert.Throws<TargetInvocationException>(() => MappedType.GetMethod("HostGuard")!.Invoke(caller, null)).InnerException is RpcException g ? g.Error : throw new Exception("Missing failure"));
        await Assert.ThrowsAsync<RpcException>(() => Invoke(caller, "Ping", 2));
    }
    [Fact]
    public async Task Inherited_private_receiver_body_and_state_are_bound()
    {
        using var wire = new Wire { Identity = new PeerId("host") };
        using var host = new RpcRuntime(NetworkRole.Host, "inherited", new PeerId("host"), new PeerId("host"), wire);
        var childType = RuntimeTests.WovenAssembly.GetType("Fixture.InheritedChild")!;
        var baseType = RuntimeTests.WovenAssembly.GetType("Fixture.InheritedBase")!;
        var child = Activator.CreateInstance(childType)!;
        var key = new TargetKey("inherited"); host.Bind(key, child, _ => true);
        Assert.Equal(4, (int)baseType.GetProperty("State")!.GetValue(child)!);
        var method = baseType.GetMethod("PrivateBody", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(7, await host.CallAsync<int>(key, method, SendTo.Host, new object[] { 3 }));
    }
    [Fact]
    public void Unsupported_compiled_shapes_are_diagnosed_without_touching_output()
    {
        var input = RuntimeTests.FixturePath("InvalidFixtures");
        var output = Path.Combine(Path.GetTempPath(), "invalid-" + Guid.NewGuid().ToString("N") + ".dll");
        var errors = Weaver.Weave(input, output);
        Assert.Contains(errors, e => e.Contains("All requires void"));
        Assert.Contains(errors, e => e.Contains("async void"));
        Assert.Contains(errors, e => e.Contains("generic"));
        Assert.Contains(errors, e => e.Contains("SyncVar requires"));
        Assert.Contains(errors, e => e.Contains("inherited SyncVar name collision"));
        Assert.False(File.Exists(output));
        File.WriteAllBytes(output, new byte[] { 1, 2, 3 });
        Assert.NotEmpty(Weaver.Weave(input, output));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Role_only_and_state_only_reweaves_are_rejected_without_output(bool roleOnly)
    {
        var source = RuntimeTests.FixturePath("Fixtures");
        var input = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        using (var module = ModuleDefinition.ReadModule(source, new ReaderParameters { InMemory = true }))
        {
            foreach (var type in module.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes)))
            {
                foreach (var method in type.Methods)
                    for (var i = method.CustomAttributes.Count - 1; i >= 0; i--)
                        if (method.CustomAttributes[i].AttributeType.FullName == (roleOnly ? typeof(RpcAttribute).FullName : typeof(HostOnlyAttribute).FullName)) method.CustomAttributes.RemoveAt(i);
                foreach (var property in type.Properties)
                    if (roleOnly) property.CustomAttributes.Clear();
            }
            if (!roleOnly)
                foreach (var type in module.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes)))
                    foreach (var method in type.Methods)
                        for (var i = method.CustomAttributes.Count - 1; i >= 0; i--)
                            if (method.CustomAttributes[i].AttributeType.FullName == typeof(RpcAttribute).FullName) method.CustomAttributes.RemoveAt(i);
            module.Write(input);
        }
        Assert.Empty(Weaver.Weave(input, first));
        Assert.Contains(Weaver.Weave(first, second), e => e.Contains("already woven"));
        Assert.False(File.Exists(second));
    }
}
