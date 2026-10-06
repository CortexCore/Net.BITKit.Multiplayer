using System.Collections.Concurrent;
using System.Buffers;
using System.Reflection;
using System.Runtime.Loader;
using BITKit.Multiplayer;
using BITKit.Multiplayer.CodeGen;
using Mono.Cecil;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BITKit.Multiplayer.Tests;

[Collection("Runtime defaults isolation")]
public class RuntimeTests
{
    internal static Assembly WovenAssembly => Fixture.Value.Counter.Assembly;
    internal static string FixturePath(string project)
    {
        var frameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = frameworkDirectory.Parent!.Name;
        return Path.GetFullPath($"../../../{project}/{configuration}/{frameworkDirectory.Name}/{project}.dll", AppContext.BaseDirectory);
    }
    private static readonly Lazy<(Type Counter, Type Contract)> Fixture = new(() =>
    {
        var input = FixturePath("Fixtures");
        var output = Path.Combine(Path.GetTempPath(), "bitkit-fixture-" + Guid.NewGuid().ToString("N") + ".dll");
        Assert.Empty(Weaver.Weave(input, output));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(output);
        return (assembly.GetType("Fixture.Counter")!, typeof(Fixture.ICounter));
    });
    private static object NewCounter() => Activator.CreateInstance(Fixture.Value.Counter)!;
    [Fact]
    public void StateReadinessAndBindingRemovalHaveScopedLifecycleNotifications()
    {
        using var runtime = Runtime(NetworkRole.Host, "lifecycle-observer", "host", new MemoryWire());
        var key = new TargetKey("observed"); var instance = NewCounter();
        int unbound = 0;
        runtime.TargetUnbound += removed =>
        {
            Assert.Equal(key, removed);
            Assert.False(runtime.IsStateSynchronized(key));
            unbound++;
        };
        runtime.Bind(key, instance, _ => true);
        Assert.True(runtime.IsStateSynchronized(key));
        runtime.Unbind(key); Assert.Equal(1, unbound);
        runtime.Bind(key, instance, _ => true);
        Assert.True(runtime.IsStateSynchronized(key));
        runtime.RemoveTarget(key); Assert.Equal(2, unbound);
        var token = runtime.LifetimeCancellation;
        runtime.Dispose(); Assert.True(token.IsCancellationRequested);
    }
    private static object NewBinaryService() => Activator.CreateInstance(WovenAssembly.GetType("Fixture.BinaryService")!)!;
    // Build a second, genuinely woven implementation with the SyncVar attribute removed
    // from a temporary copy of the compiled fixture; the checked-in fixture remains untouched.
    private static readonly Lazy<Type> RpcOnlyCounter = new(() =>
    {
        var name = "RpcOnlyFixture_" + Guid.NewGuid().ToString("N");
        var directory = Path.GetDirectoryName(FixturePath("Fixtures"))!;
        var input = Path.Combine(directory, name + ".dll");
        var output = Path.Combine(directory, name + ".woven.dll");
        using (var module = ModuleDefinition.ReadModule(FixturePath("Fixtures"), new ReaderParameters { InMemory = true }))
        {
            module.Assembly.Name.Name = name;
            module.Name = name + ".dll";
            var property = module.Types.Single(t => t.Name == "Counter").Properties.Single(p => p.Name == "Version");
            property.CustomAttributes.Remove(property.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(SyncVarAttribute).FullName));
            module.Write(input);
        }
        Assert.Empty(Weaver.Weave(input, output));
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(output).GetType("Fixture.Counter")!;
    });
    private static object NewRpcOnlyCounter() => Activator.CreateInstance(RpcOnlyCounter.Value)!;
    private static async Task<object?> Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethods().Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        object? value;
        try { value = method.Invoke(target, args); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
        if (value is Task task)
        {
            await task;
            return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
        }
        return value;
    }
    private static int Field(object instance, string name) => (int)instance.GetType().GetField(name)!.GetValue(instance)!;
    private static int Version(object instance) => (int)instance.GetType().GetProperty("Version")!.GetValue(instance)!;
    private static void SetVersion(object instance, int value)
    {
        try { instance.GetType().GetProperty("Version")!.SetValue(instance, value); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static RpcRuntime Runtime(NetworkRole role, string scope, string local, MemoryWire wire) => new(role, scope, new PeerId(local), new PeerId("host"), wire);
    private sealed class MemoryWire : IRoomWire
    {
        public bool IsConnected => true;
        public readonly Dictionary<PeerId, (MemoryWire Wire, PeerId Sender)> Links = new();
        public readonly ConcurrentQueue<(PeerId Peer, byte[] Data)> Sent = new();
        public string? ForgeOrigin;
        public string? DropKind;
        public TaskCompletionSource? HeldSend;
        public PeerId? HeldRecipient;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft;
        public Task SendAsync(PeerId peer, byte[] data)
        {
            if (!Links.TryGetValue(peer, out var link)) throw new RpcException(RpcError.Disconnected, "Not linked");
            Sent.Enqueue((peer, data.ToArray()));
            if (HeldSend != null && (!HeldRecipient.HasValue || HeldRecipient.Value.Equals(peer))) return HeldSend.Task;
            if (DropKind != null && ReliableCodec.Decode(data).Kind == DropKind) return Task.CompletedTask;
            if (ForgeOrigin != null)
            {
                if (data.Length != 0 && data[0] == TypedRpcHeader.Magic)
                {
                    data = data.ToArray();
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(36, 8),
                        TypedTarget.Peer(new PeerId(ForgeOrigin)));
                }
                else
                {
                    var envelope = ReliableCodec.Decode(data);
                    envelope.Origin = ForgeOrigin;
                    data = ReliableCodec.Encode(envelope);
                }
            }
            link.Wire.Received?.Invoke(link.Sender, data);
            return Task.CompletedTask;
        }
        public void Disconnect(PeerId peer) => PeerLeft?.Invoke(peer);
        public void Replay(PeerId peer, byte[] data) { var link = Links[peer]; link.Wire.Received?.Invoke(link.Sender, data); }
        public void Inject(PeerId sender, Packet envelope) => Received?.Invoke(sender, ReliableCodec.Encode(envelope));
        public void InjectRaw(PeerId sender, byte[] bytes) => Received?.Invoke(sender, bytes);
        public void Dispose() { }
    }
    private static void Link(MemoryWire host, MemoryWire client, string peer)
    {
        host.Links[new PeerId(peer)] = (client, new PeerId("host"));
        client.Links[new PeerId("host")] = (host, new PeerId(peer));
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        for (int i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }
    private static Packet CallPacket(string scope, TargetKey key, string method, string id, SendTo to, string destination, params object[] args)
    {
        var signature = Fixture.Value.Contract.GetMethod(method)!;
        return new Packet { Scope = scope, Kind = "call", Id = id, Origin = "host", Destination = destination,
            Service = key.Service, Entity = key.Entity, Component = key.Component,
            Method = RpcRuntime.MethodId(signature), To = to,
            Args = signature.GetParameters().Where(p => p.ParameterType != typeof(RpcTarget))
                .Zip(args.Where(a => a is not RpcTarget), (p, value) => ReliableValues.Encode(p.ParameterType, value)).ToArray() };
    }
    private static Packet SnapshotPacket(string scope, TargetKey key) => new()
    { Scope = scope, Kind = "snapshotRequest", Id = "snap", Origin = "forged", Destination = "host",
      Service = key.Service, Entity = key.Entity, Component = key.Component };
    private static Packet StatePacket(string scope, TargetKey key, long version, byte[] encoded)
    {
        // Deliberately permits oversized/invalid bodies for receiver-negative tests.
        var value = new byte[SyncWire.Header + encoded.Length]; value[0] = 0x53; value[1] = 1;
        var members = Fixture.Value.Counter.GetProperties().Where(p => p.IsDefined(typeof(SyncVarAttribute)))
            .ToDictionary(p => p.Name, p => p.GetCustomAttribute<WovenSyncVarAttribute>()!.Fingerprint);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(3), SyncWire.Mix(members["Version"], SyncWire.ContractFingerprint(members)));
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(11), -1);
        encoded.CopyTo(value, SyncWire.Header);
        return new Packet { Scope = scope, Kind = "state", Id = "state-" + version, Origin = "host", Destination = "a",
            Service = key.Service, Entity = key.Entity, Component = key.Component, Property = "Version", Version = version, Value = value };
    }
    private static bool IsState((PeerId Peer, byte[] Data) item, PeerId peer, TargetKey key)
    {
        var packet = ReliableCodec.Decode(item.Data);
        return item.Peer.Equals(peer) && packet.Kind == "state" && packet.Service == key.Service;
    }
    public interface INullReply { Task<string?> Fetch(); }
    public interface INullableNumberReply { Task<int?> Fetch(); }
    public interface IVoidProbe { void Ping(); }
    private sealed class CountingArrayPool : ArrayPool<byte>
    {
        public int Rents, Returns, LargestRequest;
        public override byte[] Rent(int minimumLength)
        { Rents++; LargestRequest = Math.Max(LargestRequest, minimumLength); return ArrayPool<byte>.Shared.Rent(minimumLength); }
        public override void Return(byte[] array, bool clearArray = false)
        { Returns++; ArrayPool<byte>.Shared.Return(array, clearArray); }
    }
    [Fact]
    public void Explicit_mixed_field_property_order_raw_unmanaged_nullable_and_bounded_pool_are_verified()
    {
        var dto = new Fixture.MixedOrderDto { Code = 1234, Label = "hello",
            Rows = new[] { new Fixture.NestedDetail { Code = 9, Label = "nested" } } };
        var bytes = ReliableValues.Encode(typeof(Fixture.MixedOrderDto), dto);
        Assert.Equal(1234, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4)));
        var copied = (Fixture.MixedOrderDto)ReliableValues.Decode(typeof(Fixture.MixedOrderDto), bytes)!;
        Assert.Equal(1234, copied.Code); Assert.Equal("hello", copied.Label); Assert.Equal("nested", copied.Rows[0].Label);
        var malformed = bytes.ToArray();
        var rowsOffset = 1 + 4 + ReliableValues.Encode(typeof(string), "hello").Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(rowsOffset, 4), int.MaxValue);
        Assert.Equal(RpcError.LimitExceeded,
            Assert.Throws<RpcException>(() => ReliableValues.Decode(typeof(Fixture.MixedOrderDto), malformed)).Error);
        var scalar = ReliableValues.Encode(typeof(Fixture.AnnotatedScalar), new Fixture.AnnotatedScalar { Value = 47 });
        Assert.Equal(4, scalar.Length); // annotated but unmanaged: MemoryPack raw value, no object header.
        Assert.Equal(47, ((Fixture.AnnotatedScalar)ReliableValues.Decode(typeof(Fixture.AnnotatedScalar), scalar)!).Value);
        Assert.Null(ReliableValues.Decode(typeof(int?), ReliableValues.Encode(typeof(int?), null)));
        Assert.Equal(7, ReliableValues.Decode(typeof(int?), ReliableValues.Encode(typeof(int?), (int?)7)));
        var unicode = ReliableValues.Encode(typeof(string), "😀日本語");
        Assert.Equal("😀日本語", ReliableValues.Decode(typeof(string), unicode));
        var lied = unicode.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lied.AsSpan(4, 4), int.MaxValue);
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => ReliableValues.Decode(typeof(string), lied)).Error);
        var invalidUtf8 = unicode.ToArray(); invalidUtf8[8] = 0xff;
        Assert.Equal(RpcError.InvalidPayload, Assert.Throws<RpcException>(() => ReliableValues.Decode(typeof(string), invalidUtf8)).Error);
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() =>
            ReliableValues.Encode(typeof(string), new string('x', ReliableValues.MaxStringChars + 1))).Error);
        var pool = new CountingArrayPool();
        byte[] owned;
        using (var writer = new BinaryBufferWriter(64, 32, pool))
        {
            writer.GetSpan(4)[0] = 71; writer.Advance(4);
            owned = writer.CopyOwned();
        }
        Assert.Equal(71, owned[0]); Assert.Equal(1, pool.Rents); Assert.Equal(1, pool.Returns);
        Assert.InRange(pool.LargestRequest, 1, 32); // no 256-byte scalar scratch allocation.
        using (var writer = new BinaryBufferWriter(32, 32, pool))
            Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => writer.GetSpan(33)).Error);
        Assert.Equal(pool.Rents, pool.Returns);
    }
    [Fact]
    public void Weaver_and_runtime_agree_on_rejected_reliable_generated_schemas()
    {
        var rejected = new[] { typeof(Fixture.UnorderedDto), typeof(Fixture.IgnoredDto),
            typeof(Fixture.PrivateIncludedDto), typeof(Fixture.CustomLayoutDto) };
        foreach (var type in rejected)
            Assert.Equal(RpcError.InvalidPayload, Assert.Throws<RpcException>(() => ReliableValues.Check(type)).Error);
        var source = Path.Combine(Path.GetTempPath(), "invalid-reliable-" + Guid.NewGuid().ToString("N") + ".dll");
        using (var module = ModuleDefinition.ReadModule(FixturePath("Fixtures"), new ReaderParameters { InMemory = true }))
        {
            var counter = module.Types.Single(t => t.Name == "Counter");
            for (int i = 0; i < rejected.Length; i++)
            {
                var method = new MethodDefinition("InvalidSchema" + i,
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
                method.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, module.ImportReference(rejected[i])));
                var rpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
                rpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.Host));
                method.CustomAttributes.Add(rpc);
                method.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ret);
                counter.Methods.Add(method);
            }
            module.Write(source);
        }
        var failures = Weaver.Weave(source, source + ".woven.dll");
        Assert.Equal(rejected.Length, failures.Count);
        for (int i = 0; i < rejected.Length; i++) Assert.Contains(failures, f => f.Contains("InvalidSchema" + i));
    }
    [Fact]
    public void Cecil_fingerprint_changes_for_raw_struct_pack_size_and_enum_underlying_or_value()
    {
        static ((ulong Method, ulong Fingerprint) Layout, (ulong Method, ulong Fingerprint) Enumeration) Variant(int pack, bool smallEnum, int enumValue)
        {
            var source = Path.Combine(Path.GetTempPath(), "typed-layout-" + Guid.NewGuid().ToString("N") + ".dll");
            using (var module = ModuleDefinition.ReadModule(FixturePath("Fixtures"), new ReaderParameters { InMemory = true }))
            {
                var layout = new TypeDefinition("Fixture", "LayoutProbe",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.SequentialLayout | Mono.Cecil.TypeAttributes.Sealed,
                    module.ImportReference(typeof(ValueType))) { PackingSize = (short)pack, ClassSize = 8 };
                layout.Fields.Add(new FieldDefinition("Value", Mono.Cecil.FieldAttributes.Public, module.TypeSystem.Int32));
                module.Types.Add(layout);
                var enumType = new TypeDefinition("Fixture", "EnumProbe",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed,
                    module.ImportReference(typeof(Enum)));
                enumType.Fields.Add(new FieldDefinition("value__", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.SpecialName |
                    Mono.Cecil.FieldAttributes.RTSpecialName, smallEnum ? module.TypeSystem.Byte : module.TypeSystem.Int32));
                var label = new FieldDefinition("A", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static |
                    Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, enumType)
                { Constant = smallEnum ? (object)(byte)enumValue : enumValue };
                enumType.Fields.Add(label); module.Types.Add(enumType);
                var counter = module.Types.Single(t => t.Name == "Counter");
                foreach (var (name, type) in new[] { ("LayoutCase", (TypeReference)layout), ("EnumCase", (TypeReference)enumType) })
                {
                    var method = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
                    method.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, type));
                    var rpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
                    rpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.Host));
                    method.CustomAttributes.Add(rpc); method.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ret);
                    counter.Methods.Add(method);
                }
                module.Write(source);
            }
            var woven = source + ".woven.dll";
            Assert.Empty(Weaver.Weave(source, woven));
            using var generated = ModuleDefinition.ReadModule(woven);
            var counterType = generated.Types.Single(t => t.Name == "Counter");
            (ulong, ulong) Get(string name)
            {
                var marker = counterType.Methods.Single(m => m.Name == name).CustomAttributes
                    .Single(a => a.AttributeType.FullName == typeof(WovenTypedRpcAttribute).FullName);
                return ((ulong)marker.ConstructorArguments[0].Value, (ulong)marker.ConstructorArguments[1].Value);
            }
            return (Get("LayoutCase"), Get("EnumCase"));
        }
        var baseline = Variant(1, false, 1);
        var packed = Variant(4, false, 1);
        var enumChanged = Variant(1, false, 2);
        var enumUnderlyingChanged = Variant(1, true, 1);
        Assert.Equal(baseline.Layout.Method, packed.Layout.Method);
        Assert.NotEqual(baseline.Layout.Fingerprint, packed.Layout.Fingerprint);
        Assert.Equal(baseline.Enumeration.Method, enumChanged.Enumeration.Method);
        Assert.NotEqual(baseline.Enumeration.Fingerprint, enumChanged.Enumeration.Fingerprint);
        Assert.Equal(baseline.Enumeration.Method, enumUnderlyingChanged.Enumeration.Method);
        Assert.NotEqual(baseline.Enumeration.Fingerprint, enumUnderlyingChanged.Enumeration.Fingerprint);
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "schema-layout", "host", hw);
        using var client = Runtime(NetworkRole.Client, "schema-layout", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var hotType = WovenAssembly.GetType("Fixture.HotProbe")!;
        var authority = Activator.CreateInstance(hotType)!; var caller = Activator.CreateInstance(hotType)!;
        host.Bind(key, authority, owner: new PeerId("a")); client.Bind(key, caller);
        var errors = new ConcurrentQueue<Exception>(); host.UnhandledDispatch += errors.Enqueue;
        hotType.GetMethod("Tick")!.CreateDelegate<Action<int>>(caller)(1);
        var forged = aw.Sent.Last(s => s.Data[0] == TypedRpcHeader.Magic).Data.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(forged.AsSpan(60, 8), packed.Layout.Fingerprint);
        hw.InjectRaw(new PeerId("a"), forged);
        Assert.Equal(1, Field(authority, "Count"));
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.InvalidPayload);
    }
    [Fact]
    public void Runtime_syncvar_schema_rejects_direct_and_nested_collections()
    {
        Assert.Equal(RpcError.InvalidPayload,
            Assert.Throws<RpcException>(() => ReliableValues.CheckStateSchema(typeof(int[]))).Error);
        Assert.Equal(RpcError.InvalidPayload,
            Assert.Throws<RpcException>(() => ReliableValues.CheckStateSchema(typeof(Fixture.NestedSnapshot))).Error);
        ReliableValues.CheckStateSchema(typeof(Fixture.BinaryState));
    }
    [Fact]
    public async Task Woven_nested_dto_reply_syncvar_snapshot_and_host_all_are_binary()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "dto", "host", hw);
        using var client = Runtime(NetworkRole.Client, "dto", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("binary-dto"); var authority = NewBinaryService(); var replica = NewBinaryService();
        host.Bind(key, authority, _ => true); client.Bind(key, replica);
        var snapshot = new Fixture.NestedSnapshot { Scope = "room", Tick = 52, Total = 4,
            Rows = new[] { new Fixture.NestedDetail { Code = 9, Label = "first" }, new Fixture.NestedDetail { Code = 11, Label = "second" } } };
        var reply = (Fixture.NestedSnapshot)(await Call(replica, "Snapshot", snapshot))!;
        Assert.Equal(52, reply.Tick); Assert.Equal(2, reply.Rows.Length);
        Assert.Equal("second", reply.Rows[1].Label);
        var scalar = await client.CallAsync<Fixture.AnnotatedScalar>(key,
            typeof(global::Fixture.IBinaryService).GetMethod(nameof(global::Fixture.IBinaryService.EchoScalar))!, SendTo.Host,
            new object?[] { new Fixture.AnnotatedScalar { Value = 47 } });
        Assert.Equal(47, scalar.Value);
        await Eventually(() => ((Fixture.BinaryState)replica.GetType().GetProperty("Current")!.GetValue(replica)!).Detail.Code == 9);
        client.Unbind(key);
        var late = NewBinaryService(); client.Bind(key, late);
        await Eventually(() => ((Fixture.BinaryState)late.GetType().GetProperty("Current")!.GetValue(late)!).Tick == 52);
        await Call(authority, "Publish", snapshot);
        await Eventually(() => Field(late, "Calls") == 1);
        Assert.Equal(3, Field(authority, "Calls")); // Host handles its own All exactly once.
        Assert.All(hw.Sent.Concat(aw.Sent), s => Assert.Contains(s.Data[0], new byte[] { 0xB5, 0xB6 }));
        Assert.Contains(aw.Sent, s => s.Data[0] == TypedRpcHeader.Magic);
    }
    [Fact]
    public async Task Nested_collection_bomb_is_rejected_before_allocation_for_call_reply_and_state()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "nested-bomb", "host", hw);
        using var client = Runtime(NetworkRole.Client, "nested-bomb", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("dto"); var authority = NewBinaryService(); var replica = NewBinaryService();
        host.Bind(key, authority, _ => true); client.Bind(key, replica);
        var method = typeof(Fixture.IBinaryService).GetMethod("Snapshot")!;
        var valid = ReliableValues.Encode(typeof(Fixture.NestedSnapshot), new Fixture.NestedSnapshot { Scope = "room" });
        var countOffset = 1 + ReliableValues.Encode(typeof(string), "room").Length + 8;
        var bomb = valid.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bomb.AsSpan(countOffset, 4), int.MaxValue);
        var forged = new Packet { Scope = "nested-bomb", Kind = "call", Id = "nested-count", Origin = "a", Destination = "host",
            Service = key.Service, Method = RpcRuntime.MethodId(method), To = SendTo.Host, Args = new[] { bomb } };
        hw.Inject(new PeerId("a"), forged);
        await Eventually(() => hw.Sent.Any(s => ReliableCodec.Decode(s.Data).Id == "nested-count"));
        var fault = ReliableCodec.Decode(hw.Sent.Last(s => ReliableCodec.Decode(s.Data).Id == "nested-count").Data);
        Assert.Equal(RpcError.LimitExceeded, fault.Error);
        Assert.Equal(0, Field(authority, "Calls"));
        var withRow = ReliableValues.Encode(typeof(Fixture.NestedSnapshot), new Fixture.NestedSnapshot { Scope = "room",
            Rows = new[] { new Fixture.NestedDetail { Code = 7, Label = "first" } } });
        var badString = withRow.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(badString.AsSpan(countOffset + 4 + 1 + 4, 4), int.MinValue);
        forged.Id = "nested-string"; forged.Args = new[] { badString };
        hw.Inject(new PeerId("a"), forged);
        await Eventually(() => hw.Sent.Any(s => ReliableCodec.Decode(s.Data).Id == "nested-string"));
        Assert.Equal(RpcError.InvalidPayload, ReliableCodec.Decode(hw.Sent.Last(s => ReliableCodec.Decode(s.Data).Id == "nested-string").Data).Error);
        Assert.Equal(0, Field(authority, "Calls"));
        forged.Id = "unsupported-version";
        var wrongVersion = ReliableCodec.Encode(forged); wrongVersion[1] = 2;
        hw.InjectRaw(new PeerId("a"), wrongVersion);
        Assert.DoesNotContain(hw.Sent, s => ReliableCodec.Decode(s.Data).Id == "unsupported-version");
        var pendingSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aw.HeldSend = pendingSend;
        var pending = client.CallAsync<Fixture.NestedSnapshot>(key, method, SendTo.Host,
            new object?[] { new Fixture.NestedSnapshot { Scope = "room" } });
        var id = ReliableCodec.Decode(aw.Sent.Last().Data).Id;
        aw.Inject(new PeerId("host"), new Packet { Scope = "nested-bomb", Kind = "reply", Origin = "host", Id = id,
            Service = key.Service, Method = RpcRuntime.MethodId(method), Result = bomb });
        Assert.Equal(RpcError.LimitExceeded, (await Assert.ThrowsAsync<RpcException>(() => pending)).Error);
        pendingSend.SetResult(); aw.HeldSend = null;
        var errors = new ConcurrentQueue<Exception>(); client.UnhandledDispatch += errors.Enqueue;
        var badState = ReliableValues.Encode(typeof(Fixture.BinaryState), new Fixture.BinaryState { Scope = "room", Tick = 1,
            Detail = new Fixture.NestedDetail { Code = 7, Label = "nested" } });
        var nestedLabelOffset = 1 + ReliableValues.Encode(typeof(string), "room").Length + 8 + 1 + 4;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(badState.AsSpan(nestedLabelOffset, 4), int.MinValue);
        aw.Inject(new PeerId("host"), new Packet { Scope = "nested-bomb", Kind = "state", Origin = "host", Service = key.Service,
            Property = "Current", Version = 99, Value = badState });
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.InvalidPayload);
        Assert.Equal(0, ((Fixture.BinaryState)replica.GetType().GetProperty("Current")!.GetValue(replica)!).Detail.Code);
    }
    [Fact]
    public async Task Binary_call_snapshots_mutable_dto_before_asynchronous_wire_send_and_core_has_no_json_reference()
    {
        Assert.DoesNotContain(typeof(RpcRuntime).Assembly.GetReferencedAssemblies(), a => a.Name == "Newtonsoft.Json");
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "freeze", "host", hw);
        using var client = Runtime(NetworkRole.Client, "freeze", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aw.HeldSend = held;
        var request = new Fixture.NestedSnapshot { Scope = "original", Rows = new[] { new Fixture.NestedDetail { Code = 12, Label = "old" } } };
        var method = typeof(Fixture.IBinaryService).GetMethod("Snapshot")!;
        var pending = client.CallAsync<Fixture.NestedSnapshot>(new TargetKey("remote"), method, SendTo.Host, new object?[] { request });
        request.Scope = "changed"; request.Rows[0].Label = "new";
        var frame = ReliableCodec.Decode(aw.Sent.Last().Data);
        var frozen = (Fixture.NestedSnapshot)ReliableValues.Decode(typeof(Fixture.NestedSnapshot), frame.Args![0])!;
        Assert.Equal("original", frozen.Scope); Assert.Equal("old", frozen.Rows[0].Label);
        client.Dispose();
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => pending)).Error);
        held.TrySetResult();
    }
    [Fact]
    public async Task Woven_reliable_binary_allocation_diagnostics_include_state_and_reply()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "alloc", "host", hw);
        using var client = Runtime(NetworkRole.Client, "alloc", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("counter"); host.Bind(key, NewCounter(), _ => true);
        var method = Fixture.Value.Contract.GetMethod("Add")!;
        var args = new object?[] { 1 };
        for (int i = 0; i < 128; i++) await client.CallAsync<int>(key, method, SendTo.Host, args);
        const int iterations = 1000;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < iterations; i++) await client.CallAsync<int>(key, method, SendTo.Host, args);
        var remote = GC.GetTotalAllocatedBytes(precise: true) - before;
        before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < iterations; i++) await host.CallAsync<int>(key, method, SendTo.Host, args);
        var local = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(remote > 0 && local > 0);
        Console.WriteLine($"Woven reliable binary Add + SyncVar state + reply, warmed total-process allocation: remote {remote / iterations} B/call; Host-local {local / iterations} B/call; fake wire, not zero-GC or a JSON benchmark.");
    }
    [Fact]
    public async Task Rpc_only_target_removal_revokes_local_execution_and_future_bindings()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "rpc-only-removal", "host", hw);
        using var a = Runtime(NetworkRole.Client, "rpc-only-removal", "a", aw);
        using var b = Runtime(NetworkRole.Client, "rpc-only-removal", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a")));
        host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        await Eventually(() => a.IsReady && b.IsReady);
        var key = new TargetKey("peer-only", "entity-1", "component-1");
        host.RegisterRelayPolicy(key, _ => true); // no Host implementation and no SyncVars
        var receiver = NewRpcOnlyCounter();
        a.Bind(key, receiver);
        Assert.Equal(12, await Call(receiver, "Read", new RpcTarget(new PeerId("a")), 6));
        Assert.DoesNotContain(hw.Sent, packet => IsState(packet, new PeerId("a"), key));
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: false));
        Assert.True(b.IsConnected); Assert.False(b.IsReady);
        host.RemoveTarget(key);
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => Call(receiver, "Read", new RpcTarget(new PeerId("a")), 7))).Error);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => a.Bind(key, NewRpcOnlyCounter())).Error);
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: true));
        await Eventually(() => b.IsReady);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => b.Bind(key, NewRpcOnlyCounter())).Error);
        var cw = new MemoryWire(); Link(hw, cw, "c");
        using var c = Runtime(NetworkRole.Client, "rpc-only-removal", "c", cw);
        host.RegisterMember(new RoomMember(new PeerId("c"), ready: true)); // joins AFTER removal
        Assert.False(c.IsReady);
        c.ConfirmReady();
        await Eventually(() => c.IsReady);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => c.Bind(key, NewRpcOnlyCounter())).Error);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => host.RegisterRelayPolicy(key, _ => true)).Error);
    }
    [Fact]
    public async Task Ready_demotion_revokes_local_target_and_re_ready_restores_without_a_new_runtime()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "ready-transition", "host", hw);
        using var client = Runtime(NetworkRole.Client, "ready-transition", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"), "player-a", ready: true));
        Assert.False(client.IsReady); // directory may precede explicit local confirmation
        client.ConfirmReady();
        await Eventually(() => client.IsReady);
        var key = new TargetKey("local-target"); var local = NewCounter();
        client.Bind(key, local);
        Assert.Equal(6, await Call(local, "Read", new RpcTarget(new PeerId("a")), 3));
        var delayed = Fixture.Value.Contract.GetMethod("Delayed")!;
        var authority = NewCounter(); host.Bind(key, authority, _ => true);
        var pending = client.CallAsync<int>(key, delayed, SendTo.Host, new object[] { 8 });
        host.RegisterMember(new RoomMember(new PeerId("a"), "player-a", ready: false));
        await Eventually(() => !client.IsReady && client.TryGetMember(new PeerId("a"), out var member) && !member!.Ready);
        Assert.True(client.IsConnected); // transport remains connected; membership is not ready
        Assert.Equal(RpcError.Unauthorized, (await Assert.ThrowsAsync<RpcException>(() => pending)).Error);
        Assert.False(client.TryGetReadyPeerByPlayerId("player-a", out _));
        Assert.Equal(RpcError.Unauthorized, (await Assert.ThrowsAsync<RpcException>(() => Call(local, "Read", new RpcTarget(new PeerId("a")), 3))).Error);
        client.ConfirmReady(); Assert.False(client.IsReady); // local confirmation cannot override Host
        host.RegisterMember(new RoomMember(new PeerId("a"), "player-a", ready: true));
        await Eventually(() => client.IsReady);
        Assert.Equal(8, await Call(local, "Read", new RpcTarget(new PeerId("a")), 4));
        Assert.Equal(2, await client.CallAsync<int>(key, Fixture.Value.Contract.GetMethod("Add")!, SendTo.Host, new object[] { 2 }));
    }
    [Fact]
    public async Task Member_removal_revokes_session_clears_directory_and_rejects_stale_host_snapshots()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "member-removal", "host", hw);
        using var a = Runtime(NetworkRole.Client, "member-removal", "a", aw);
        using var b = Runtime(NetworkRole.Client, "member-removal", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a"), ready: true));
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: true));
        a.ConfirmReady(); b.ConfirmReady();
        await Eventually(() => a.IsReady && b.IsReady);
        var key = new TargetKey("retired-entity"); var authority = NewCounter(); var local = NewCounter();
        host.Bind(key, authority, _ => true); a.Bind(key, local);
        var prior = hw.Sent.Last(packet => packet.Peer.Value == "a" && ReliableCodec.Decode(packet.Data).Kind == "members").Data;
        var pending = a.CallAsync<int>(key, Fixture.Value.Contract.GetMethod("Delayed")!, SendTo.Host, new object[] { 5 });
        host.RemoveMember(new PeerId("a"));
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => pending)).Error);
        Assert.False(a.IsConnected); Assert.False(a.IsReady); Assert.Empty(a.Members);
        Assert.Equal(0, a.MemberVersion);
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => Call(local, "Read", new RpcTarget(new PeerId("a")), 3))).Error);
        hw.Replay(new PeerId("a"), prior); // authenticated but stale directory must not resurrect the retired session
        Assert.Empty(a.Members); Assert.False(a.IsConnected);
        Assert.Throws<RpcException>(() => a.ConfirmReady());
        await Eventually(() => !b.TryGetMember(new PeerId("a"), out _));
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => host.RegisterMember(new RoomMember(new PeerId("a")))).Error);
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: false));
        Assert.True(b.IsConnected); Assert.False(b.IsReady);
        host.RemoveMember(new PeerId("b")); // revocation also reaches a member already unready
        Assert.False(b.IsConnected); Assert.Empty(b.Members);
    }
    [Fact]
    public async Task Directory_replicates_versions_identities_readiness_and_rejects_forgery()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire(); Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "directory", "host", hw);
        using var a = Runtime(NetworkRole.Client, "directory", "a", aw);
        using var b = Runtime(NetworkRole.Client, "directory", "b", bw);
        var directoryErrors = new ConcurrentQueue<Exception>(); a.UnhandledDispatch += directoryErrors.Enqueue;
        host.RegisterMember(new RoomMember(new PeerId("a"), "player-a", "steam-a", false));
        Assert.Equal(host.MemberVersion, a.MemberVersion); // roster can arrive before explicit local confirmation
        Assert.False(a.IsReady);
        a.ConfirmReady();
        Assert.False(a.IsReady); Assert.True(a.IsConnected); // local confirmation cannot confer Host readiness
        Assert.Throws<RpcException>(() => host.RegisterMember(new RoomMember(new PeerId("b"), "player-a", "other", true)));
        Assert.Throws<RpcException>(() => host.RegisterMember(new RoomMember(new PeerId("b"), "other", "steam-a", true)));
        host.RegisterMember(new RoomMember(new PeerId("a"), "player-a", "steam-a", true));
        for (int i = 0; i < 100 && a.MemberVersion != host.MemberVersion; i++) await Task.Delay(10);
        Assert.True(a.MemberVersion == host.MemberVersion,
            $"client={a.MemberVersion} host={host.MemberVersion} errors={string.Join(";", directoryErrors)} packets={string.Join(";", hw.Sent.Select(s => ReliableCodec.Decode(s.Data).Kind))}");
        Assert.True(a.TryGetReadyPeerByPlayerId("player-a", out var found)); Assert.Equal("a", found.Value);
        Assert.True(a.TryGetReadyPeerBySteamId("steam-a", out found)); Assert.Equal("a", found.Value);
        Assert.True(a.TryGetMember(new PeerId("a"), out var member)); Assert.Equal("player-a", member!.PlayerId);
        Assert.Throws<NotSupportedException>(() => ((IList<RoomMember>)a.Members).Clear());
        host.RegisterMember(new RoomMember(new PeerId("b"), "player-b", "steam-b", true));
        b.ConfirmReady();
        await Eventually(() => a.Members.Count == 3 && b.Members.Count == 3);
        long version = a.MemberVersion;
        var fake = new Packet { Scope = "directory", Kind = "members", Id = "fake", Origin = "host",
            Destination = "a", Version = version + 50,
            Members = new List<MemberRecord> { new() { Peer = "host", PlayerId = "forged", SteamId = "s", Ready = true } },
            RemovedTargets = new List<TargetRecord>() };
        aw.Inject(new PeerId("b"), fake); // not the authenticated Host transport session
        hw.Inject(new PeerId("a"), fake); // Host must never accept a client-authored directory
        Assert.Equal(version, a.MemberVersion); Assert.Equal(version, host.MemberVersion);
        host.RegisterMember(new RoomMember(new PeerId("b"), "player-b", "steam-b", false));
        await Eventually(() => a.MemberVersion > version);
        Assert.False(a.TryGetReadyPeerBySteamId("steam-b", out _));
        version = a.MemberVersion;
        fake.Version = version - 1;
        aw.Inject(new PeerId("host"), fake); // stale authenticated snapshot
        Assert.Equal(version, a.MemberVersion);
        host.RemoveMember(new PeerId("b"));
        await Eventually(() => !a.TryGetMember(new PeerId("b"), out _));
        Assert.False(a.TryGetReadyPeerByPlayerId("player-b", out _));
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() =>
            host.RegisterMember(new RoomMember(new PeerId("b"), "player-b", "steam-b"))).Error);
        var beforeDrop = a.MemberVersion;
        hw.DropKind = "members";
        host.RegisterMember(new RoomMember(new PeerId("c"), "player-c", ready: false));
        Assert.Equal(beforeDrop, a.MemberVersion);
        hw.DropKind = null;
        a.RequestMembers();
        await Eventually(() => a.MemberVersion == host.MemberVersion && a.TryGetMember(new PeerId("c"), out _));
    }
    [Fact]
    public void Member_capacity_and_reused_player_identity_fail_explicitly()
    {
        using var host = Runtime(NetworkRole.Host, "capacity-members", "host", new MemoryWire());
        for (int i = 0; i < 255; i++) host.RegisterMember(new RoomMember(new PeerId("p" + i), "player" + i, ready: false));
        Assert.Equal(256, host.Members.Count);
        var error = Assert.Throws<RpcException>(() => host.RegisterMember(new RoomMember(new PeerId("overflow"), ready: false)));
        Assert.Equal(RpcError.LimitExceeded, error.Error);
        Assert.Throws<RpcException>(() => host.RegisterMember(new RoomMember(new PeerId("p1"), "player2", ready: true)));
    }
    [Fact]
    public async Task Owner_host_authority_and_explicit_policy_are_distinct()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "owner");
        using var host = Runtime(NetworkRole.Host, "owner-mode", "host", hw);
        using var client = Runtime(NetworkRole.Client, "owner-mode", "owner", aw);
        host.RegisterMember(new RoomMember(new PeerId("owner"))); client.ConfirmReady();
        var key = new TargetKey("entity", "id", "counter"); var authority = NewCounter();
        host.Bind(key, authority, owner: new PeerId("owner"));
        Assert.Equal(2, await Call(authority, "Add", 2));
        var other = new TargetKey("entity", "other", "counter");
        var deniedObject = NewCounter(); host.Bind(other, deniedObject, _ => false, new PeerId("owner"));
        var denied = await Assert.ThrowsAsync<RpcException>(() => Call(deniedObject, "Add", 1));
        Assert.Equal(RpcError.Unauthorized, denied.Error);
    }
    [Fact]
    public async Task Declared_rpc_route_cannot_be_changed_by_wire_proxy_or_local_call()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "route", "host", hw);
        using var client = Runtime(NetworkRole.Client, "route", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("route"); var authority = NewCounter(); host.Bind(key, authority, _ => true);
        // Announce is declared All. A client forges a Host call to the same method ID.
        hw.Inject(new PeerId("a"), CallPacket("route", key, "Announce", "forged-route", SendTo.Host, "host", "illegal"));
        await Eventually(() => hw.Sent.Any(s => ReliableCodec.Decode(s.Data).Id == "forged-route"));
        var rejected = ReliableCodec.Decode(hw.Sent.Single(s => ReliableCodec.Decode(s.Data).Id == "forged-route").Data);
        Assert.Equal(RpcError.InvalidPayload, rejected.Error);
        Assert.Equal(0, Field(authority, "Announces"));
        // A valid interface method ID cannot be recast as a local Target call either.
        var wrongLocal = await Assert.ThrowsAsync<RpcException>(() => host.CallAsync<int>(key, Fixture.Value.Contract.GetMethod("Add")!, SendTo.Target,
            new object[] { new RpcTarget(new PeerId("host")), 1 }));
        Assert.Equal(RpcError.InvalidPayload, wrongLocal.Error);
        Assert.Equal(0, Field(authority, "Adds"));
        var proxy = client.CreateProxy<Fixture.ICounter>(key,
            new Dictionary<string, SendTo> { [RpcRuntime.MethodId(Fixture.Value.Contract.GetMethod("BroadcastAsync")!)] = SendTo.Target });
        var proxyFailure = await Assert.ThrowsAsync<RpcException>(() => proxy.BroadcastAsync());
        Assert.Equal(RpcError.InvalidPayload, proxyFailure.Error);
        Assert.Equal(0, Field(authority, "Announces"));
    }
    [Fact]
    public async Task Unready_requester_cannot_relay_even_with_permissive_policy()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "unready-relay", "host", hw);
        using var a = Runtime(NetworkRole.Client, "unready-relay", "a", aw);
        using var b = Runtime(NetworkRole.Client, "unready-relay", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a"), ready: false));
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: true));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("peer-only"); var receiver = NewCounter(); var caller = NewCounter(); int checkedPolicy = 0;
        host.RegisterRelayPolicy(key, _ => { Interlocked.Increment(ref checkedPolicy); return true; });
        a.Bind(key, caller); b.Bind(key, receiver);
        var failure = await Assert.ThrowsAsync<RpcException>(() => Call(caller, "Read", new RpcTarget(new PeerId("b")), 9));
        Assert.Equal(RpcError.Unauthorized, failure.Error);
        Assert.Equal(0, checkedPolicy);
        Assert.Equal("", receiver.GetType().GetField("Sender")!.GetValue(receiver));
        host.RegisterMember(new RoomMember(new PeerId("a"), ready: true));
        Assert.Equal(18, await Call(caller, "Read", new RpcTarget(new PeerId("b")), 9));
        Assert.Equal(1, checkedPolicy);
    }
    [Fact]
    public async Task Snapshots_and_deltas_require_ready_member_and_bound_state_permission()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "state-auth", "host", hw);
        using var a = Runtime(NetworkRole.Client, "state-auth", "a", aw);
        using var b = Runtime(NetworkRole.Client, "state-auth", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a"), ready: true));
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: false));
        a.ConfirmReady(); b.ConfirmReady(); // local confirmation cannot make b ready on Host
        var key = new TargetKey("private-state"); var authority = NewCounter(); var replicaA = NewCounter(); var replicaB = NewCounter();
        host.Bind(key, authority, request => request.Method != RpcRuntime.StateAuthorizationMethod || request.Sender.Peer.Value == "b");
        SetVersion(authority, 7);
        a.Bind(key, replicaA); b.Bind(key, replicaB);
        hw.Inject(new PeerId("a"), SnapshotPacket("state-auth", key));
        hw.Inject(new PeerId("b"), SnapshotPacket("state-auth", key));
        Assert.DoesNotContain(hw.Sent, s => IsState(s, new PeerId("a"), key) || IsState(s, new PeerId("b"), key));
        Assert.Equal(0, Version(replicaA)); Assert.Equal(0, Version(replicaB));
        host.RegisterMember(new RoomMember(new PeerId("b"), ready: true));
        await Eventually(() => Version(replicaB) == 7);
        Assert.Equal(0, Version(replicaA));
        SetVersion(authority, 8);
        await Eventually(() => Version(replicaB) == 8);
        Assert.Equal(0, Version(replicaA));
        Assert.DoesNotContain(hw.Sent, s => IsState(s, new PeerId("a"), key));
    }
    [Fact]
    public async Task Woven_interface_concrete_all_state_and_late_bind()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "room-1", "host", hw);
        using var a = Runtime(NetworkRole.Client, "room-1", "a", aw);
        using var b = Runtime(NetworkRole.Client, "room-1", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a")));
        host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("counter");
        var authority = NewCounter(); var aObject = NewCounter();
        host.Bind(key, authority, _ => true);
        a.Bind(key, aObject);
        Assert.Equal(3, await Call(aObject, "Add", 3)); // concrete call, remote body on Host
        Assert.Equal(1, Field(authority, "Adds")); Assert.Equal(0, Field(aObject, "Adds"));
        Assert.Equal("a", authority.GetType().GetField("Sender")!.GetValue(authority));
        await Eventually(() => Version(aObject) == 3);
        var interfaceCall = Fixture.Value.Contract.GetMethod("Add")!;
        var task = (Task)interfaceCall.Invoke(aObject, new object[] { 2 })!;
        await task;
        Assert.Equal(5, task.GetType().GetProperty("Result")!.GetValue(task));
        Assert.Equal(2, Field(authority, "Adds"));
        Assert.Equal(2, await Call(authority, "Read", new RpcTarget(new PeerId("a")), 1));
        await Call(authority, "Announce", "hello");
        await Eventually(() => Field(aObject, "Announces") == 1);
        Assert.Equal(1, Field(authority, "Announces")); // Host exactly once
        await Call(authority, "BroadcastAsync");
        Assert.Equal(1, Field(aObject, "Announces"));
        Assert.Equal(2, Field(authority, "Announces"));
        var bObject = NewCounter(); b.Bind(key, bObject); // late bind snapshot
        await Eventually(() => Version(bObject) == 5);
        Assert.Throws<RpcException>(() => SetVersion(aObject, 50));
        SetVersion(authority, 5); Assert.Equal(5, Version(authority));
        SetVersion(authority, 8); await Eventually(() => Version(bObject) == 8);
        await Assert.ThrowsAsync<RpcException>(() => Call(aObject, "Announce", "forbidden"));
        var fault = await Assert.ThrowsAsync<RpcException>(() => Call(aObject, "Fail"));
        Assert.Equal(RpcError.RemoteFault, fault.Error);
        a.Unbind(key);
        await Assert.ThrowsAsync<RpcException>(() => Call(aObject, "Add", 1));
    }
    [Fact]
    public async Task Isolated_rooms_and_interface_only_remote_proxy()
    {
        var h1 = new MemoryWire(); var c1 = new MemoryWire(); var h2 = new MemoryWire(); var c2 = new MemoryWire();
        Link(h1, c1, "a"); Link(h2, c2, "a");
        using var host1 = Runtime(NetworkRole.Host, "first", "host", h1);
        using var client1 = Runtime(NetworkRole.Client, "first", "a", c1);
        using var host2 = Runtime(NetworkRole.Host, "second", "host", h2);
        using var client2 = Runtime(NetworkRole.Client, "second", "a", c2);
        host1.RegisterMember(new RoomMember(new PeerId("a")));
        host2.RegisterMember(new RoomMember(new PeerId("a")));
        client1.ConfirmReady(); client2.ConfirmReady();
        var key = new TargetKey("same"); var first = NewCounter(); var second = NewCounter(); var firstReplica = NewCounter();
        host1.Bind(key, first, _ => true); host2.Bind(key, second, _ => true);
        client1.Bind(key, firstReplica);
        // DispatchProxy instantiated using a runtime-created generic method; caller owns only contract.
        var id = RpcRuntime.MethodId(Fixture.Value.Contract.GetMethod("Add")!);
        var factory = typeof(RpcRuntime).GetMethod(nameof(RpcRuntime.CreateProxy))!.MakeGenericMethod(Fixture.Value.Contract);
        var proxy = factory.Invoke(client2, new object[] { key, new Dictionary<string, SendTo> { [id] = SendTo.Host } })!;
        var result = (Task<int>)Fixture.Value.Contract.GetMethod("Add")!.Invoke(proxy, new object[] { 9 })!;
        Assert.Equal(9, await result);
        Assert.Equal(0, Field(first, "Adds")); Assert.Equal(1, Field(second, "Adds"));
        // Physically inject a correctly keyed, correctly authenticated packet for the *other* room.
        // A shared-wire or scope-blind implementation would increment first here.
        var wrongRoom = CallPacket("second", key, "Add", "31", SendTo.Host, "host", 4);
        h1.Inject(new PeerId("a"), wrongRoom);
        Assert.Equal(0, Field(first, "Adds"));
        Assert.DoesNotContain(h1.Sent, item => ReliableCodec.Decode(item.Data).Id == "31");
        c1.Inject(new PeerId("host"), StatePacket("second", key, 99, ReliableValues.Encode(typeof(int), 99)));
        Assert.Equal(0, Version(firstReplica));
        SetVersion(first, 3);
        await Eventually(() => Version(firstReplica) == 3);
    }
    [Fact]
    public async Task Relay_preserves_original_sender_and_host_policy()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = Runtime(NetworkRole.Host, "relay", "host", hw);
        using var a = Runtime(NetworkRole.Client, "relay", "a", aw);
        using var b = Runtime(NetworkRole.Client, "relay", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("target"); var receiver = NewCounter(); var caller = NewCounter();
        host.RegisterRelayPolicy(key, x => x.Sender.Peer.Value == "a" && x.Executor.Value == "b");
        b.Bind(key, receiver, x => x.Sender.Peer.Value == "a"); a.Bind(key, caller);
        aw.ForgeOrigin = "host"; // forged packet identity is overwritten from the physical session
        Assert.Equal(14, await Call(caller, "Read", new RpcTarget(new PeerId("b")), 7));
        Assert.Equal("a", receiver.GetType().GetField("Sender")!.GetValue(receiver));
        Assert.Equal(0, Field(caller, "Adds"));
    }
    [Fact]
    public async Task Timeout_and_disconnect_complete_waiters()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "wait", "host", hw);
        using var a = Runtime(NetworkRole.Client, "wait", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a")));
        a.ConfirmReady();
        var key = new TargetKey("wait"); var h = NewCounter(); var c = NewCounter(); host.Bind(key, h, _ => true); a.Bind(key, c);
        a.Timeout = TimeSpan.FromMilliseconds(25);
        var timeout = await Assert.ThrowsAsync<RpcException>(() => Call(c, "Slow"));
        Assert.Equal(RpcError.Timeout, timeout.Error);
        a.Timeout = TimeSpan.FromSeconds(5);
        var pending = Call(c, "Slow");
        aw.Disconnect(new PeerId("host"));
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => pending)).Error);
        Assert.False(a.IsConnected); Assert.Empty(a.Members); Assert.Equal(0, a.MemberVersion);
        var newWire = new MemoryWire(); Link(hw, newWire, "b");
        host.RegisterMember(new RoomMember(new PeerId("b")));
        var next = Runtime(NetworkRole.Client, "wait", "b", newWire);
        next.ConfirmReady();
        var another = NewCounter(); next.Bind(key, another);
        var interrupted = Call(another, "Slow");
        next.Dispose();
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => interrupted)).Error);
    }
    [Fact]
    public async Task Hung_send_does_not_block_cancellation_timeout_unbind_disconnect_or_disposal()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "hung-send", "host", hw);
        var a = Runtime(NetworkRole.Client, "hung-send", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); a.ConfirmReady();
        var key = new TargetKey("slow-send"); var authority = NewCounter(); var caller = NewCounter();
        host.Bind(key, authority, _ => true); a.Bind(key, caller);
        var add = Fixture.Value.Contract.GetMethod("Add")!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aw.HeldSend = held; a.Timeout = TimeSpan.FromMilliseconds(40);
        using var cancel = new CancellationTokenSource();
        var canceled = a.CallAsync<int>(key, add, SendTo.Host, new object[] { 1 }, cancel.Token);
        Assert.False(canceled.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(2)));
        var timedOut = a.CallAsync<int>(key, add, SendTo.Host, new object[] { 2 });
        Assert.Equal(RpcError.Timeout, (await Assert.ThrowsAsync<RpcException>(() => timedOut.WaitAsync(TimeSpan.FromSeconds(2)))).Error);
        a.Timeout = TimeSpan.FromSeconds(5);
        var unbound = a.CallAsync<int>(key, add, SendTo.Host, new object[] { 3 });
        a.Unbind(key);
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => unbound.WaitAsync(TimeSpan.FromSeconds(2)))).Error);
        var disconnected = a.CallAsync<int>(key, add, SendTo.Host, new object[] { 4 });
        aw.Disconnect(new PeerId("host"));
        Assert.Equal(RpcError.Disconnected, (await Assert.ThrowsAsync<RpcException>(() => disconnected.WaitAsync(TimeSpan.FromSeconds(2)))).Error);
        // A second client verifies scope disposal during the same blocked send.
        var bw = new MemoryWire(); Link(hw, bw, "b");
        using var b = Runtime(NetworkRole.Client, "hung-send", "b", bw);
        host.RegisterMember(new RoomMember(new PeerId("b"))); b.ConfirmReady();
        var blockedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bw.HeldSend = blockedB;
        var disposed = b.CallAsync<int>(key, add, SendTo.Host, new object[] { 5 });
        b.Dispose();
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => disposed.WaitAsync(TimeSpan.FromSeconds(2)))).Error);
        Assert.Equal(0, Field(authority, "Adds"));
        held.TrySetException(new IOException("late send failure"));
        blockedB.TrySetException(new IOException("late send failure"));
        a.Dispose();
    }
    [Fact]
    public async Task Local_call_wait_timeout_and_cancellation_do_not_abort_authority_body()
    {
        var hw = new MemoryWire();
        using var host = Runtime(NetworkRole.Host, "local-wait", "host", hw);
        var key = new TargetKey("local"); host.Bind(key, NewCounter(), _ => true);
        var delayed = Fixture.Value.Contract.GetMethod("Delayed")!;
        host.Timeout = TimeSpan.FromMilliseconds(30);
        var timeout = await Assert.ThrowsAsync<RpcException>(() => host.CallAsync<int>(key, delayed, SendTo.Host, new object[] { 1 }).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(RpcError.Timeout, timeout.Error);
        host.Timeout = TimeSpan.FromSeconds(2);
        using var cancellation = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CallAsync<int>(key, delayed, SendTo.Host, new object[] { 2 }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, await host.CallAsync<int>(key, delayed, SendTo.Host, new object[] { 3 }));
    }
    [Fact]
    public void Reweave_rejected()
    {
        var original = FixturePath("Fixtures");
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        Assert.Empty(Weaver.Weave(original, first));
        Assert.Contains(Weaver.Weave(first, second), x => x.Contains("already woven"));
    }
    [Fact]
    public void Unsupported_signatures_and_syncvar_rejected_by_actual_weaver()
    {
        var original = FixturePath("Fixtures");
        var invalid = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        using (var module = ModuleDefinition.ReadModule(original, new ReaderParameters { InMemory = true }))
        {
            var type = module.Types.Single(t => t.Name == "Counter");
            type.Methods.Single(m => m.Name == "Fail").ReturnType = module.TypeSystem.Int32; // invalid return
            var setter = type.Properties.Single(p => p.Name == "Version").SetMethod!;
            setter.Body.Instructions.Insert(0, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            setter.Body.Instructions.Insert(1, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Pop)); // no longer a plain auto-setter
            type.Methods.Single(m => m.Name == "Add").Parameters[0].ParameterType = new ByReferenceType(module.TypeSystem.Int32);
            module.Write(invalid);
        }
        var errors = Weaver.Weave(invalid, invalid + ".woven");
        Assert.Contains(errors, x => x.Contains("unsupported RPC return"));
        Assert.Contains(errors, x => x.Contains("SyncVar requires"));
        Assert.Contains(errors, x => x.Contains("ref/out"));
    }
    [Fact]
    public async Task Explicit_cancellation_stops_waiting_not_remote_execution()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "cancel", "host", hw);
        using var client = Runtime(NetworkRole.Client, "cancel", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a")));
        client.ConfirmReady();
        var key = new TargetKey("cancel"); host.Bind(key, NewCounter(), _ => true);
        var method = Fixture.Value.Contract.GetMethod("Delayed")!;
        using var cancellation = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallAsync<int>(key, method, SendTo.Host, new object[] { 5 }, cancellation.Token));
        Assert.Equal(6, await client.CallAsync<int>(key, method, SendTo.Host, new object[] { 6 }));
    }
    [Fact]
    public async Task Late_ready_snapshot_and_stale_delta_cannot_roll_back()
    {
        var hw = new MemoryWire(); var cw = new MemoryWire(); Link(hw, cw, "late");
        using var host = Runtime(NetworkRole.Host, "state-epoch", "host", hw);
        using var client = Runtime(NetworkRole.Client, "state-epoch", "late", cw);
        var key = new TargetKey("persistent"); var authoritative = NewCounter(); var replica = NewCounter();
        host.Bind(key, authoritative, _ => true);
        SetVersion(authoritative, 4);
        host.Unbind(key);
        authoritative = NewCounter();
        host.Bind(key, authoritative, _ => true);
        Assert.Equal(4, Version(authoritative)); // authority state survives temporary unbind in the same scope
        client.Bind(key, replica); // not ready at Host yet; request is ignored
        Assert.Equal(0, Version(replica));
        host.RegisterMember(new RoomMember(new PeerId("late"), ready: true));
        client.ConfirmReady();
        await Eventually(() => Version(replica) == 4);
        var stale = hw.Sent.First(s => s.Peer.Value == "late" && ReliableCodec.Decode(s.Data).Kind == "state").Data;
        SetVersion(authoritative, 6); await Eventually(() => Version(replica) == 6);
        // An old snapshot replayed over the same connection is discarded by the property version.
        hw.Replay(new PeerId("late"), stale);
        Assert.Equal(6, Version(replica));
        client.Unbind(key);
        var rebound = NewCounter(); client.Bind(key, rebound);
        Assert.Equal(6, Version(rebound)); // Unbind retains state for the same identity.
        host.RemoveTarget(key);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => client.Bind(key, NewCounter())).Error);
    }
    [Fact]
    public async Task Denial_and_scoped_dependency_injection_alias()
    {
        var hostWire = new MemoryWire(); var clientWire = new MemoryWire(); Link(hostWire, clientWire, "a");
        // Configure generic registration using the actual woven fixture's runtime-only types.
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => Runtime(NetworkRole.Host, "di", "host", hostWire));
        typeof(MultiplayerServiceCollectionExtensions).GetMethod(nameof(MultiplayerServiceCollectionExtensions.AddScopedRpcService))!
            .MakeGenericMethod(Fixture.Value.Contract, Fixture.Value.Counter)
            .Invoke(null, new object?[] { registrations, new TargetKey("alias"), (Func<AuthorizationRequest, bool>)(_ => false), null });
        using var provider = registrations.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var concrete = scope.ServiceProvider.GetRequiredService(Fixture.Value.Counter);
        Assert.Same(concrete, scope.ServiceProvider.GetRequiredService(Fixture.Value.Contract));
        var host = scope.ServiceProvider.GetRequiredService<RpcRuntime>();
        host.RegisterMember(new RoomMember(new PeerId("a")));
        using var client = Runtime(NetworkRole.Client, "di", "a", clientWire);
        client.ConfirmReady();
        var proxy = typeof(RpcRuntime).GetMethod(nameof(RpcRuntime.CreateProxy))!.MakeGenericMethod(Fixture.Value.Contract)
            .Invoke(client, new object[] { new TargetKey("alias"), new Dictionary<string, SendTo> { [RpcRuntime.MethodId(Fixture.Value.Contract.GetMethod("Add")!)] = SendTo.Host } })!;
        var task = (Task<int>)Fixture.Value.Contract.GetMethod("Add")!.Invoke(proxy, new object[] { 1 })!;
        var denied = await Assert.ThrowsAsync<RpcException>(() => task);
        Assert.Equal(RpcError.Unauthorized, denied.Error);
        Assert.Equal(0, Field(concrete, "Adds"));
    }
    [Fact]
    public async Task Binary_rejects_old_json_without_type_metadata_and_supports_null_result()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "safe-binary", "host", hw);
        using var client = Runtime(NetworkRole.Client, "safe-binary", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("binary"); var authority = NewCounter(); host.Bind(key, authority, _ => true);
        Assert.Equal(3, await client.CallAsync<int>(key, Fixture.Value.Contract.GetMethod("Add")!, SendTo.Host, new object[] { 3 }));
        // The sole legacy JSON frame in these tests: a v1 call with attacker CLR metadata.
        var rejectedOld = new ConcurrentQueue<Exception>(); host.UnhandledDispatch += rejectedOld.Enqueue;
        hw.InjectRaw(new PeerId("a"), System.Text.Encoding.UTF8.GetBytes("{\"V\":1,\"Kind\":\"call\",\"$type\":\"System.IO.FileInfo, System.IO.FileSystem\"}"));
        Assert.Equal(1, Field(authority, "Adds"));
        Assert.Contains(rejectedOld, e => e is RpcException r && r.Error == RpcError.InvalidPayload && r.Message.Contains("binary v3 required"));
        var method = typeof(INullReply).GetMethod(nameof(INullReply.Fetch))!;
        var heldNull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aw.HeldSend = heldNull;
        var pending = client.CallAsync<string?>(key, method, SendTo.Host, Array.Empty<object>());
        var query = aw.Sent.Select(s => ReliableCodec.Decode(s.Data)).Last(p => p.Kind == "call" && p.Method == RpcRuntime.MethodId(method));
        aw.Inject(new PeerId("host"), new Packet { Scope = "safe-binary", Kind = "reply", Id = query.Id, Origin = "host",
            Service = key.Service, Entity = key.Entity, Component = key.Component, Method = query.Method,
            Result = ReliableValues.Encode(typeof(string), null) });
        Assert.Null(await pending);
        var nullableMethod = typeof(INullableNumberReply).GetMethod(nameof(INullableNumberReply.Fetch))!;
        var nullablePending = client.CallAsync<int?>(key, nullableMethod, SendTo.Host, Array.Empty<object>());
        var nullableRequest = aw.Sent.Select(s => ReliableCodec.Decode(s.Data)).Last(p => p.Kind == "call" && p.Method == RpcRuntime.MethodId(nullableMethod));
        aw.Inject(new PeerId("host"), new Packet { Scope = "safe-binary", Kind = "reply", Id = nullableRequest.Id, Origin = "host",
            Service = key.Service, Method = nullableRequest.Method, Result = ReliableValues.Encode(typeof(int?), null) });
        Assert.Null(await nullablePending);
        var nonNullable = client.CallAsync<int>(key, Fixture.Value.Contract.GetMethod("Add")!, SendTo.Host, new object[] { 1 });
        var intRequest = aw.Sent.Select(s => ReliableCodec.Decode(s.Data)).Last(p => p.Kind == "call" && p.Method == RpcRuntime.MethodId(Fixture.Value.Contract.GetMethod("Add")!));
        aw.Inject(new PeerId("host"), new Packet { Scope = "safe-binary", Kind = "reply", Id = intRequest.Id, Origin = "host",
            Service = key.Service, Entity = key.Entity, Component = key.Component, Method = intRequest.Method, Result = null });
        Assert.Equal(RpcError.InvalidPayload, (await Assert.ThrowsAsync<RpcException>(() => nonNullable)).Error);
        heldNull.TrySetResult();
    }
    [Fact]
    public async Task Void_remote_faults_reach_safe_observers_for_host_and_all()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "void-errors", "host", hw);
        using var client = Runtime(NetworkRole.Client, "void-errors", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("void"); var authority = NewCounter(); var receiver = NewCounter();
        host.Bind(key, authority, _ => true);
        client.Bind(key, receiver, _ => false);
        var hostFaults = new ConcurrentQueue<Exception>();
        host.UnhandledDispatch += _ => throw new InvalidOperationException("observer must not break transport");
        host.UnhandledDispatch += hostFaults.Enqueue;
        var clientFaults = new ConcurrentQueue<Exception>();
        client.UnhandledDispatch += _ => throw new InvalidOperationException("observer must not break transport");
        client.UnhandledDispatch += clientFaults.Enqueue;
        await Call(authority, "Announce", "forbidden by receiver");
        await Eventually(() => clientFaults.Any(e => e is RpcException r && r.Error == RpcError.Unauthorized));
        Assert.Equal(1, Field(authority, "Announces")); Assert.Equal(0, Field(receiver, "Announces"));
        Assert.DoesNotContain(aw.Sent, s => s.Data[0] == 0xB5 && ReliableCodec.Decode(s.Data).Kind == "reply");
        var probe = client.CreateProxy<IVoidProbe>(key, new Dictionary<string, SendTo>
        { [RpcRuntime.MethodId(typeof(IVoidProbe).GetMethod(nameof(IVoidProbe.Ping))!)] = SendTo.Host });
        probe.Ping();
        await Eventually(() => hostFaults.Any(e => e is RpcException r && r.Error == RpcError.MissingMethod));
        Assert.Equal(1, Field(authority, "Announces"));
    }
    [Fact]
    public async Task Pending_limit_is_bounded_even_when_sends_never_finish()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); Link(hw, aw, "a");
        using var host = Runtime(NetworkRole.Host, "pending-limit", "host", hw);
        var client = Runtime(NetworkRole.Client, "pending-limit", "a", aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        client.Timeout = TimeSpan.FromSeconds(30);
        var key = new TargetKey("no-send"); var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aw.HeldSend = held;
        var method = Fixture.Value.Contract.GetMethod("Add")!;
        var pending = new List<Task<int>>();
        for (int i = 0; i < 1024; i++) pending.Add(client.CallAsync<int>(key, method, SendTo.Host, new object[] { i }));
        var limit = await Assert.ThrowsAsync<RpcException>(() => client.CallAsync<int>(key, method, SendTo.Host, new object[] { 1024 }));
        Assert.Equal(RpcError.LimitExceeded, limit.Error);
        client.Dispose();
        foreach (var wait in pending) Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => wait)).Error);
        held.TrySetException(new IOException("late send"));
    }
    [Fact]
    public void Bound_target_and_buffered_state_limits_fail_explicitly()
    {
        using var host = Runtime(NetworkRole.Host, "target-cap", "host", new MemoryWire());
        for (int i = 0; i < 512; i++) host.Bind(new TargetKey("target-" + i), NewCounter(), _ => true);
        Assert.Equal(RpcError.LimitExceeded,
            Assert.Throws<RpcException>(() => host.Bind(new TargetKey("target-overflow"), NewCounter(), _ => true)).Error);
        var hw = new MemoryWire(); var cw = new MemoryWire(); Link(hw, cw, "a");
        using var client = Runtime(NetworkRole.Client, "state-cap", "a", cw);
        client.ConfirmReady();
        var errors = new ConcurrentQueue<Exception>(); client.UnhandledDispatch += errors.Enqueue;
        for (int i = 0; i <= 4096; i++)
            cw.Inject(new PeerId("host"), StatePacket("state-cap", new TargetKey("target-" + i), 1, ReliableValues.Encode(typeof(int), i)));
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.LimitExceeded);
    }
    [Fact]
    public async Task Relays_are_bounded_independently_of_each_clients_pending_requests()
    {
        var hw = new MemoryWire(); var aw = new MemoryWire(); var cw = new MemoryWire(); var bw = new MemoryWire();
        Link(hw, aw, "a"); Link(hw, cw, "c"); Link(hw, bw, "b");
        var host = Runtime(NetworkRole.Host, "relay-cap", "host", hw);
        var a = Runtime(NetworkRole.Client, "relay-cap", "a", aw);
        var c = Runtime(NetworkRole.Client, "relay-cap", "c", cw);
        host.RegisterMember(new RoomMember(new PeerId("a")));
        host.RegisterMember(new RoomMember(new PeerId("c")));
        host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); c.ConfirmReady();
        host.Timeout = TimeSpan.FromSeconds(30); a.Timeout = TimeSpan.FromSeconds(30); c.Timeout = TimeSpan.FromSeconds(30);
        var key = new TargetKey("relay-cap"); host.RegisterRelayPolicy(key, _ => true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hw.HeldRecipient = new PeerId("b"); hw.HeldSend = held;
        var method = Fixture.Value.Contract.GetMethod("Read")!;
        var pending = new List<Task<int>>();
        for (int i = 0; i < 1024; i++)
            pending.Add((i % 2 == 0 ? a : c).CallAsync<int>(key, method, SendTo.Target,
                new object[] { new RpcTarget(new PeerId("b")), i }));
        var exceeded = await Assert.ThrowsAsync<RpcException>(() => a.CallAsync<int>(key, method, SendTo.Target,
            new object[] { new RpcTarget(new PeerId("b")), 1024 }));
        Assert.Equal(RpcError.LimitExceeded, exceeded.Error);
        a.Dispose(); c.Dispose(); host.Dispose();
        foreach (var wait in pending) Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => wait)).Error);
        held.TrySetException(new IOException("late relay send"));
    }
    [Fact]
    public async Task Concurrent_bind_state_and_dispose_do_not_resurrect_removed_identity()
    {
        var hw = new MemoryWire(); var cw = new MemoryWire(); Link(hw, cw, "a");
        var host = Runtime(NetworkRole.Host, "lifecycle", "host", hw);
        using var client = Runtime(NetworkRole.Client, "lifecycle", "a", cw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("entity", "stable-id", "component"); var authority = NewCounter(); var replica = NewCounter();
        host.Bind(key, authority, _ => true); client.Bind(key, replica);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new Barrier(3);
        var writer = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (int i = 1; i <= 350; i++)
                try { SetVersion(authority, i); }
                catch (RpcException ex) when (ex.Error == RpcError.Disposed) { }
                catch (Exception ex) { errors.Enqueue(ex); }
        });
        var rebinder = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 80; i++)
            {
                try { host.Unbind(key); host.Bind(key, authority, _ => true); }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        });
        var inbound = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (int version = 1; version <= 500; version++)
                cw.Inject(new PeerId("host"), StatePacket("lifecycle", key, version, ReliableValues.Encode(typeof(int), version)));
        });
        await Task.WhenAll(writer, rebinder, inbound);
        Assert.Empty(errors);
        Assert.Equal(500, Version(replica));
        host.RemoveTarget(key);
        Assert.Throws<RpcException>(() => SetVersion(authority, 900));
        Assert.Throws<RpcException>(() => SetVersion(replica, 900));
        cw.Inject(new PeerId("host"), StatePacket("lifecycle", key, 900, ReliableValues.Encode(typeof(int), 900)));
        Assert.Equal(500, Version(replica));
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => client.Bind(key, NewCounter())).Error);
        Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => host.Bind(key, NewCounter(), _ => true)).Error);
        host.Dispose();
    }
    [Fact]
    public async Task Invalid_state_payload_does_not_poison_version_or_leave_failed_bind_attached()
    {
        var hw = new MemoryWire(); var cw = new MemoryWire(); Link(hw, cw, "a");
        using var host = Runtime(NetworkRole.Host, "invalid-state", "host", hw);
        using var client = Runtime(NetworkRole.Client, "invalid-state", "a", cw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("state-validation"); var replica = NewCounter(); client.Bind(key, replica);
        var errors = new ConcurrentQueue<Exception>(); client.UnhandledDispatch += errors.Enqueue;
        Packet State(int version, byte[] value) => StatePacket("invalid-state", key, version, value);
        cw.Inject(new PeerId("host"), State(200, Array.Empty<byte>())); // truncated non-nullable int
        await Eventually(() => errors.Any());
        Assert.Equal(0, Version(replica));
        cw.Inject(new PeerId("host"), State(1, ReliableValues.Encode(typeof(int), 7)));
        Assert.Equal(7, Version(replica));
        cw.Inject(new PeerId("host"), State(300, ReliableValues.Encode(typeof(string), new string('x', 17 * 1024))));
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.LimitExceeded);
        Assert.Equal(7, Version(replica));
        var nextKey = new TargetKey("unbound-invalid");
        cw.Inject(new PeerId("host"), StatePacket("invalid-state", nextKey, 5, ReliableValues.Encode(typeof(string), "not-an-int")));
        var invalid = NewCounter();
        Assert.ThrowsAny<Exception>(() => client.Bind(nextKey, invalid));
        Assert.Throws<RpcException>(() => Call(invalid, "Add", 1).GetAwaiter().GetResult());
    }
    [Fact]
    public void Actual_DI_scopes_keep_same_key_isolated_after_one_scope_disposes()
    {
        var registrations = new ServiceCollection();
        var counter = 0;
        registrations.AddScoped(_ => new RpcRuntime(NetworkRole.Host, "di-room-" + Interlocked.Increment(ref counter), new PeerId("host"), new PeerId("host"), new MemoryWire()));
        typeof(MultiplayerServiceCollectionExtensions).GetMethod(nameof(MultiplayerServiceCollectionExtensions.AddScopedRpcService))!
            .MakeGenericMethod(Fixture.Value.Contract, Fixture.Value.Counter)
            .Invoke(null, new object?[] { registrations, new TargetKey("same-key"), (Func<AuthorizationRequest, bool>)(_ => true), null });
        using var provider = registrations.BuildServiceProvider();
        var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var first = scope1.ServiceProvider.GetRequiredService(Fixture.Value.Counter);
        var second = scope2.ServiceProvider.GetRequiredService(Fixture.Value.Counter);
        Assert.Same(first, scope1.ServiceProvider.GetRequiredService(Fixture.Value.Contract));
        Assert.Same(second, scope2.ServiceProvider.GetRequiredService(Fixture.Value.Contract));
        Assert.NotSame(first, second);
        SetVersion(first, 7);
        Assert.Equal(0, Version(second));
        scope1.Dispose();
        Assert.Throws<RpcException>(() => SetVersion(first, 8));
        SetVersion(second, 9);
        Assert.Equal(9, Version(second));
    }
}

[CollectionDefinition("Runtime defaults isolation", DisableParallelization = true)]
public sealed class RuntimeDefaultsIsolation { }
