using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Runtime.Loader;
using BITKit.Multiplayer;
using BITKit.Multiplayer.CodeGen;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MemoryPack;
using Xunit;

namespace BITKit.Multiplayer.Tests;

[MemoryPackable]
public partial class CodableDto
{
    [MemoryPackOrder(0)] public int Id { get; set; }
}
[MemoryPackable]
public partial class RejectedNestedDto
{
    public int[] Items { get; set; } = Array.Empty<int>();
}

[Collection("Runtime defaults isolation")]
public sealed class UnreliableRuntimeTests
{
    public struct MotionPayload
    {
        public int Id;
        public float X, Z, AimX, AimZ;
    }
    private static readonly Lazy<Type> Woven = new(() =>
    {
        var input = RuntimeTests.FixturePath("Fixtures");
        var directory = Path.Combine(Path.GetTempPath(), "bitkit-udp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "UdpFixture.dll");
        var output = Path.Combine(directory, "UdpFixture.woven.dll");
        using (var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true }))
        {
            module.Assembly.Name.Name = "UdpFixture" + Guid.NewGuid().ToString("N");
            module.Name = Path.GetFileName(source);
            var counter = module.Types.Single(t => t.Name == "Counter");
            var announce = counter.Methods.Single(m => m.Name == "Announce");
            announce.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(RpcAttribute).FullName).Properties.Add(
                new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            var segment = module.ImportReference(typeof(ArraySegment<int>));
            var motion = new MethodDefinition("Motion", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            motion.Parameters.Add(new ParameterDefinition("poses", Mono.Cecil.ParameterAttributes.None, segment));
            var attribute = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.All));
            attribute.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            motion.CustomAttributes.Add(attribute);
            counter.Methods.Add(motion);
            var field = counter.Fields.Single(f => f.Name == "Announces");
            var get = module.ImportReference(typeof(ArraySegment<int>).GetProperty("Item")!.GetMethod!);
            var il = motion.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarga_S, motion.Parameters[0]); il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, get); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            var pose = new MethodDefinition("MotionDto", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
            var poseSegment = module.ImportReference(typeof(ArraySegment<MotionPayload>));
            pose.Parameters.Add(new ParameterDefinition("poses", Mono.Cecil.ParameterAttributes.None, poseSegment));
            var poseRpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
            poseRpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.All));
            poseRpc.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            pose.CustomAttributes.Add(poseRpc); counter.Methods.Add(pose);
            var element = new VariableDefinition(module.ImportReference(typeof(MotionPayload)));
            pose.Body.Variables.Add(element); pose.Body.InitLocals = true;
            var poseIl = pose.Body.GetILProcessor();
            poseIl.Emit(OpCodes.Ldarg_0); poseIl.Emit(OpCodes.Ldarga_S, pose.Parameters[0]); poseIl.Emit(OpCodes.Ldc_I4_0);
            poseIl.Emit(OpCodes.Call, module.ImportReference(typeof(ArraySegment<MotionPayload>).GetProperty("Item")!.GetMethod!));
            poseIl.Emit(OpCodes.Stloc, element); poseIl.Emit(OpCodes.Ldloca_S, element);
            poseIl.Emit(OpCodes.Ldfld, module.ImportReference(typeof(MotionPayload).GetField("Id")!));
            poseIl.Emit(OpCodes.Stfld, field); poseIl.Emit(OpCodes.Ret);
            var dtoMethod = new MethodDefinition("DtoMotion", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
            dtoMethod.Parameters.Add(new ParameterDefinition("dto", Mono.Cecil.ParameterAttributes.None, module.ImportReference(typeof(CodableDto))));
            var dtoRpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
            dtoRpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.All));
            dtoRpc.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            dtoMethod.CustomAttributes.Add(dtoRpc); counter.Methods.Add(dtoMethod);
            var dtoIl = dtoMethod.Body.GetILProcessor();
            dtoIl.Emit(OpCodes.Ldarg_0); dtoIl.Emit(OpCodes.Ldarg_1);
            dtoIl.Emit(OpCodes.Callvirt, module.ImportReference(typeof(CodableDto).GetProperty("Id")!.GetMethod!));
            dtoIl.Emit(OpCodes.Stfld, field); dtoIl.Emit(OpCodes.Ret);
            foreach (var route in new[] { SendTo.Host, SendTo.Target })
            {
                var name = route == SendTo.Host ? "HostMotion" : "TargetMotion";
                var call = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
                if (route == SendTo.Target) call.Parameters.Add(new ParameterDefinition("target", Mono.Cecil.ParameterAttributes.None, module.ImportReference(typeof(RpcTarget))));
                call.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
                var rpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
                rpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), route));
                rpc.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
                call.CustomAttributes.Add(rpc);
                counter.Methods.Add(call);
                var sender = counter.Fields.Single(f => f.Name == "Sender");
                var body = call.Body.GetILProcessor();
                var senderPeer = new VariableDefinition(module.ImportReference(typeof(PeerId)));
                call.Body.Variables.Add(senderPeer); call.Body.InitLocals = true;
                body.Emit(OpCodes.Ldarg_0);
                body.Emit(OpCodes.Call, module.ImportReference(typeof(RpcCallContext).GetProperty("Current")!.GetMethod!));
                body.Emit(OpCodes.Callvirt, module.ImportReference(typeof(RpcCallContext).GetProperty("Sender")!.GetMethod!));
                body.Emit(OpCodes.Stloc, senderPeer);
                body.Emit(OpCodes.Ldloca_S, senderPeer);
                body.Emit(OpCodes.Call, module.ImportReference(typeof(PeerId).GetProperty("Value")!.GetMethod!));
                body.Emit(OpCodes.Stfld, sender);
                body.Emit(OpCodes.Ldarg_0); body.Emit(route == SendTo.Host ? OpCodes.Ldarg_1 : OpCodes.Ldarg_2);
                body.Emit(OpCodes.Stfld, field); body.Emit(OpCodes.Ret);
            }
            module.Write(source);
        }
        var diagnostics = Weaver.Weave(source, output);
        Assert.True(diagnostics.Count == 0, string.Join(" | ", diagnostics));
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(output).GetType("Fixture.Counter")!;
    });
    private sealed class Wire : IRoomWire, IRoomDatagrams
    {
        public readonly Dictionary<PeerId, (Wire Wire, PeerId Sender)> Links = new();
        public int ReliableCalls;
        public int DatagramCalls;
        public bool Delay;
        public bool Deliver { get; set; } = true;
        public bool Fault { get; set; }
        private readonly List<(PeerId Peer, ReadOnlyMemory<byte> Memory, TaskCompletionSource Completion)> _pending = new();
        public ReadOnlyMemory<byte> Pending => _pending[0].Memory;
        public ReadOnlyMemory<byte> PendingFor(string peer) => _pending.First(p => p.Peer.Value == peer).Memory;
        public int PendingCount => _pending.Count;
        public bool IsConnected => true;
        public int MaxUnreliablePayloadBytes => 900;
        public bool UnreliableEnabled { get; set; } = true;
        public bool IsUnreliableReady(PeerId peer) => Links.ContainsKey(peer);
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft;
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public Task SendAsync(PeerId peer, byte[] data)
        {
            if (ReliableCodec.Decode(data).Kind == "call") ReliableCalls++;
            var link = Links[peer]; link.Wire.Received?.Invoke(link.Sender, data);
            return Task.CompletedTask;
        }
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> data)
        {
            DatagramCalls++;
            if (Fault) return Task.FromException(new IOException("Injected send fault"));
            if (!Deliver) return Task.CompletedTask;
            if (Delay)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add((peer, data, completion));
                return completion.Task;
            }
            var link = Links[peer]; link.Wire.UnreliableReceived?.Invoke(link.Sender, data);
            return Task.CompletedTask;
        }
        public void Flush(string? peerName = null)
        {
            var pending = _pending.First(p => peerName == null || p.Peer.Value == peerName);
            var peer = pending.Peer;
            var link = Links[peer]; link.Wire.UnreliableReceived?.Invoke(link.Sender, pending.Memory);
            _pending.Remove(pending);
            pending.Completion.SetResult();
        }
        public void CancelPending()
        {
            var pending = _pending[0]; _pending.RemoveAt(0);
            pending.Completion.SetCanceled();
        }
        public void Inject(PeerId sender, byte[] bytes) => UnreliableReceived?.Invoke(sender, bytes);
        public void InjectReliable(PeerId sender, byte[] bytes) => Received?.Invoke(sender, bytes);
        public void Leave(PeerId peer) => PeerLeft?.Invoke(peer);
        public void Dispose() { }
        public DatagramStatistics GetDatagramStatistics() => new();
        public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private static void Link(Wire host, Wire client, string peer)
    {
        host.Links[new PeerId(peer)] = (client, new PeerId("host"));
        client.Links[new PeerId("host")] = (host, new PeerId(peer));
    }
    private static void Invoke(object obj, string method, object arg)
    {
        try { obj.GetType().GetMethod(method)!.Invoke(obj, new[] { arg }); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static int Count(object obj) => (int)obj.GetType().GetField("Announces")!.GetValue(obj)!;
    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Outstanding;
        public int Rents;
        public int Returns;
        public int PeakOutstanding;
        public long OutstandingCapacity;
        public long PeakOutstandingCapacity;
        public int LargestRent;
        private static void Maximum(ref int field, int candidate)
        {
            int old;
            do { old = Volatile.Read(ref field); if (candidate <= old) return; }
            while (Interlocked.CompareExchange(ref field, candidate, old) != old);
        }
        private static void Maximum(ref long field, long candidate)
        {
            long old;
            do { old = Interlocked.Read(ref field); if (candidate <= old) return; }
            while (Interlocked.CompareExchange(ref field, candidate, old) != old);
        }
        public override byte[] Rent(int minimumLength)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
            Interlocked.Increment(ref Rents);
            Maximum(ref PeakOutstanding, Interlocked.Increment(ref Outstanding));
            Maximum(ref PeakOutstandingCapacity, Interlocked.Add(ref OutstandingCapacity, buffer.Length));
            Maximum(ref LargestRent, buffer.Length);
            return buffer;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            ArrayPool<byte>.Shared.Return(array, clearArray);
            Interlocked.Increment(ref Returns); Interlocked.Decrement(ref Outstanding);
            Interlocked.Add(ref OutstandingCapacity, -array.Length);
        }
    }
    private static CountingPool Track(RpcRuntime runtime)
    {
        var pool = new CountingPool();
        typeof(RpcRuntime).GetField("_unreliablePool", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, pool);
        return pool;
    }
    private static int Leases(RpcRuntime runtime) => (int)typeof(RpcRuntime)
        .GetField("_unreliableLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
    private static int PendingReplies(RpcRuntime runtime)
    {
        var pending = typeof(RpcRuntime).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        return (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!;
    }
    [Fact]
    public void Warm_preboxed_measurement_reports_managed_allocation_and_returns_writer_lease()
    {
        using var runtime = new RpcRuntime(NetworkRole.Host, "measure-bench", new PeerId("host"), new PeerId("host"), new Wire());
        var method = Woven.Value.GetMethod("Motion")!;
        var key = new TargetKey("motion");
        var source = new int[64]; source[7] = 19;
        var args = new object?[] { new ArraySegment<int>(source, 7, 1) }; // preboxed/reused, not hidden in the measured loop
        for (int i = 0; i < 300; i++) runtime.MeasureUnreliableCall(key, method, args);
        int baseline = TypedPacketBuffer.ActiveRentals;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) runtime.MeasureUnreliableCall(key, method, args);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(baseline, TypedPacketBuffer.ActiveRentals);
        Console.WriteLine($"Woven typed UDP MeasureUnreliableCall, preboxed segment one: {bytes} bytes / {bytes / 5000.0:F2} B per warmed measurement; no wire send.");
    }
    [Fact]
    public void Cecil_woven_unreliable_methods_have_typed_direct_receivers_without_object_array_or_box()
    {
        using var module = ModuleDefinition.ReadModule(Woven.Value.Assembly.Location);
        var type = module.Types.Single(t => t.Name == "Counter");
        foreach (var name in new[] { "Announce", "Motion", "MotionDto", "DtoMotion", "HostMotion", "TargetMotion" })
        {
            var method = type.Methods.Single(m => m.Name == name);
            Assert.Contains(method.CustomAttributes, a => a.AttributeType.FullName == typeof(WovenTypedRpcAttribute).FullName);
            Assert.DoesNotContain(method.Body.Instructions, i => i.OpCode == OpCodes.Newarr || i.OpCode == OpCodes.Box ||
                i.Operand is MethodReference reference && reference.Name == "Invoke");
            var receiver = type.Methods.Single(m => m.Name.StartsWith("__bitkit_recv_" + name + "_", StringComparison.Ordinal));
            Assert.Contains(receiver.Body.Instructions, i => i.Operand is MethodReference reference && reference.Name.StartsWith("__bitkit_body_" + name + "_", StringComparison.Ordinal));
            Assert.DoesNotContain(receiver.Body.Instructions, i => i.OpCode == OpCodes.Newarr || i.OpCode == OpCodes.Box ||
                i.Operand is MethodReference reference && reference.Name == "Invoke");
        }
    }
    private static byte[] UnreliableFrame(string scope, TargetKey key, MethodInfo method, SendTo route, string destination, string origin,
        object? argument, bool hostileCollection = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0xB4); writer.Write((byte)2); writer.Write((byte)route);
        foreach (var text in new[] { scope, key.Service, key.Entity, key.Component, RpcRuntime.MethodId(method), destination, origin })
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            writer.Write(checked((ushort)utf8.Length)); writer.Write(utf8);
        }
        writer.Write((byte)1);
        if (hostileCollection) { writer.Write(4); writer.Write(int.MaxValue); }
        else
        {
            var buffer = new ArrayBufferWriter<byte>();
            MemoryPackSerializer.Serialize(method.GetParameters().Last().ParameterType, buffer, argument);
            writer.Write(buffer.WrittenCount); writer.Write(buffer.WrittenSpan);
        }
        return stream.ToArray(); // test-only frame construction; production writer never does this.
    }

    [Fact]
    public void Weaver_rejects_task_and_unknown_delivery_at_build_time()
    {
        var input = RuntimeTests.FixturePath("Fixtures");
        var directory = Path.Combine(Path.GetTempPath(), "bitkit-invalid-udp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "InvalidUdp.dll");
        using (var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true }))
        {
            var counter = module.Types.Single(t => t.Name == "Counter");
            var add = counter.Methods.Single(m => m.Name == "Add");
            add.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(RpcAttribute).FullName).Properties.Add(
                new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            var announce = counter.Methods.Single(m => m.Name == "Announce");
            announce.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(RpcAttribute).FullName).Properties.Add(
                new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), (RpcDelivery)999)));
            var unsupported = new MethodDefinition("UnsupportedDto", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
            unsupported.Parameters.Add(new ParameterDefinition("dto", Mono.Cecil.ParameterAttributes.None, counter));
            var rpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
            rpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.Host));
            rpc.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            unsupported.CustomAttributes.Add(rpc); unsupported.Body.GetILProcessor().Emit(OpCodes.Ret); counter.Methods.Add(unsupported);
            var nested = new MethodDefinition("NestedCollection", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
            nested.Parameters.Add(new ParameterDefinition("dto", Mono.Cecil.ParameterAttributes.None, module.ImportReference(typeof(RejectedNestedDto))));
            var nestedRpc = new CustomAttribute(module.ImportReference(typeof(RpcAttribute).GetConstructor(new[] { typeof(SendTo) })!));
            nestedRpc.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(typeof(SendTo)), SendTo.Host));
            nestedRpc.Properties.Add(new Mono.Cecil.CustomAttributeNamedArgument("Delivery", new CustomAttributeArgument(module.ImportReference(typeof(RpcDelivery)), RpcDelivery.Unreliable)));
            nested.CustomAttributes.Add(nestedRpc); nested.Body.GetILProcessor().Emit(OpCodes.Ret); counter.Methods.Add(nested);
            module.Write(source);
        }
        var errors = Weaver.Weave(source, Path.Combine(directory, "InvalidUdp.woven.dll"));
        Assert.Equal(4, errors.Count);
        Assert.Equal(2, errors.Count(error => error.Contains("Delivery")));
        Assert.Equal(2, errors.Count(error => error.Contains("MemoryPack schema")));
    }

    [Fact]
    public void Woven_all_uses_only_datagram_lane_borrowed_segment_and_ready_barrier()
    {
        var hostWire = new Wire(); var aw = new Wire(); var bw = new Wire();
        Link(hostWire, aw, "a"); Link(hostWire, bw, "b");
        using var host = new RpcRuntime(NetworkRole.Host, "udp", new PeerId("host"), new PeerId("host"), hostWire);
        using var a = new RpcRuntime(NetworkRole.Client, "udp", new PeerId("a"), new PeerId("host"), aw);
        using var b = new RpcRuntime(NetworkRole.Client, "udp", new PeerId("b"), new PeerId("host"), bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("motion"); var h = Activator.CreateInstance(Woven.Value)!;
        var first = Activator.CreateInstance(Woven.Value)!; var second = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, h, _ => true); a.Bind(key, first); b.Bind(key, second);
        host.HoldBroadcasts(new PeerId("b"));
        var values = new int[32]; values[7] = 42;
        var method = Woven.Value.GetMethod("Motion")!;
        int size = host.MeasureUnreliableCall(key, method, new object?[] { new ArraySegment<int>(values, 7, 1) });
        Assert.InRange(size, 1, 900);
        hostWire.Delay = true;
        Invoke(h, "Motion", new ArraySegment<int>(values, 7, 1));
        values[7] = -1;
        Assert.Equal(42, Count(h)); Assert.Equal(0, Count(first)); Assert.Equal(0, Count(second));
        Assert.Equal(1, hostWire.DatagramCalls); Assert.Equal(0, hostWire.ReliableCalls);
        Assert.Equal(size, hostWire.Pending.Length);
        Assert.Equal(TypedRpcHeader.Magic, hostWire.Pending.Span[0]); // normal woven UDP uses v4 typed, not cold v2.
        hostWire.Flush();
        Assert.Equal(42, Count(first)); Assert.Equal(0, Count(second));
        host.ReleaseBroadcasts(new PeerId("b")); hostWire.Delay = false;
        Invoke(h, "Motion", new ArraySegment<int>(new[] { 19 }, 0, 1));
        Assert.Equal(19, Count(h)); Assert.Equal(19, Count(first)); Assert.Equal(19, Count(second));
        Assert.Equal(0, hostWire.ReliableCalls);
        var interfaceCall = host.CreateProxy<Fixture.ICounter>(key,
            new Dictionary<string, SendTo> { [RpcRuntime.MethodId(typeof(Fixture.ICounter).GetMethod("Announce")!)] = SendTo.All });
        interfaceCall.Announce("interface");
        Assert.Equal(0, hostWire.ReliableCalls);
        Assert.Equal(20, Count(h));
        Assert.Equal(20, Count(first)); Assert.Equal(20, Count(second));
        // Includes woven object[] boxing, routing strings and fake-wire decode; not a zero-allocation path.
        for (int i = 0; i < 10; i++) Invoke(h, "Motion", new ArraySegment<int>(values, 7, 1));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Invoke(h, "Motion", new ArraySegment<int>(values, 7, 1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated > 0);
        Console.WriteLine($"Woven Motion call, 100 sends: {allocated} managed bytes ({allocated / 100} bytes/send, includes synchronous fake-wire dispatch + test reflection).");
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() =>
            Invoke(h, "Announce", new string('z', 2000))).Error);
        Assert.True(host.MeasureUnreliableCall(key, Woven.Value.GetMethod("Announce")!, new object?[] { new string('z', 2000) }) > 900);
        Assert.Equal(0, hostWire.ReliableCalls);
        hostWire.UnreliableEnabled = false;
        Assert.Equal(RpcError.Disconnected, Assert.Throws<RpcException>(() => Invoke(h, "Motion", new ArraySegment<int>(values, 7, 1))).Error);
        hostWire.UnreliableEnabled = true;
        using var noLane = new RpcRuntime(NetworkRole.Host, "no-lane", new PeerId("host"), new PeerId("host"), new ReliableOnlyWire());
        var missing = Activator.CreateInstance(Woven.Value)!;
        noLane.Bind(key, missing, _ => true);
        Assert.Equal(RpcError.Disconnected, Assert.Throws<RpcException>(() => Invoke(missing, "Motion", new ArraySegment<int>(values, 7, 1))).Error);
    }
    [Fact]
    public void Typed_measure_matches_actual_udp_length_for_segment_windows_unicode_and_target_values()
    {
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals); // no previous test may leave an outstanding typed rental
        var hw = new Wire(); var aw = new Wire(); var uw = new Wire();
        Link(hw, aw, "a"); Link(hw, uw, "東京");
        using var host = new RpcRuntime(NetworkRole.Host, "測定-scope", new PeerId("host"), new PeerId("host"), hw);
        using var a = new RpcRuntime(NetworkRole.Client, "測定-scope", new PeerId("a"), new PeerId("host"), aw);
        using var unicode = new RpcRuntime(NetworkRole.Client, "測定-scope", new PeerId("東京"), new PeerId("host"), uw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("東京")));
        a.ConfirmReady(); unicode.ConfirmReady();
        var key = new TargetKey("motion", "entity"); var sender = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => true);
        a.Bind(key, Activator.CreateInstance(Woven.Value)!);
        unicode.Bind(key, Activator.CreateInstance(Woven.Value)!);
        var poses = new MotionPayload[32];
        for (int i = 0; i < poses.Length; i++) poses[i] = new MotionPayload { Id = i, X = i * .5f, Z = -i };
        var motion = Woven.Value.GetMethod("MotionDto")!;
        hw.Delay = true;
        foreach (var window in new[] { new ArraySegment<MotionPayload>(poses, 2, 1),
                                       new ArraySegment<MotionPayload>(poses, 19, 1),
                                       new ArraySegment<MotionPayload>(poses, 13, 3) })
        {
            int length = host.MeasureUnreliableCall(key, motion, new object?[] { window });
            Invoke(sender, "MotionDto", window);
            Assert.Equal(TypedRpcHeader.Magic, hw.PendingFor("a").Span[0]);
            Assert.Equal(length, hw.PendingFor("a").Length);
            Assert.Equal(length, hw.PendingFor("東京").Length);
            hw.Flush("a"); hw.Flush("東京");
        }
        var announce = Woven.Value.GetMethod("Announce")!;
        foreach (var value in new[] { "a", "東京/😀" })
        {
            int length = host.MeasureUnreliableCall(key, announce, new object?[] { value });
            Invoke(sender, "Announce", value);
            Assert.Equal(length, hw.PendingFor("a").Length);
            hw.Flush("a"); hw.Flush("東京");
        }
        var targetMethod = Woven.Value.GetMethod("TargetMotion")!;
        var firstSize = host.MeasureUnreliableCall(key, targetMethod, new object?[] { new RpcTarget(new PeerId("a")), 17 });
        var unicodeSize = host.MeasureUnreliableCall(key, targetMethod, new object?[] { new RpcTarget(new PeerId("東京")), 17 });
        Assert.Equal(firstSize, unicodeSize); // destination is a fixed-width numeric header field
        foreach (var peer in new[] { "a", "東京" })
        {
            targetMethod.Invoke(sender, new object[] { new RpcTarget(new PeerId(peer)), 17 });
            Assert.Equal(firstSize, hw.PendingFor(peer).Length);
            hw.Flush(peer);
        }
        Assert.Equal(RpcError.InvalidPayload,
            Assert.Throws<RpcException>(() => host.MeasureUnreliableCall(key, targetMethod, new object?[] { default(RpcTarget), 17 })).Error);
        Assert.Equal(RpcError.InvalidPayload,
            Assert.Throws<RpcException>(() => host.MeasureUnreliableCall(key, targetMethod, new object?[] { 17 })).Error);
        Assert.True(SpinWait.SpinUntil(() => TypedPacketBuffer.ActiveRentals == 0, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public void Unmanaged_pose_segment_serializes_active_window_and_fits_byte_budget()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "poses", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "poses", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
        var receiver = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => true); client.Bind(key, receiver);
        var source = new MotionPayload[128];
        for (int i = 0; i < source.Length; i++) source[i] = new MotionPayload { Id = -777, X = 12.123456f, Z = -45.123456f, AimX = 67.123456f, AimZ = -89.123456f };
        source[19].Id = 42;
        var segment = new ArraySegment<MotionPayload>(source, 19, 30);
        var method = Woven.Value.GetMethod("MotionDto")!;
        var bytes = host.MeasureUnreliableCall(key, method, new object?[] { segment });
        Assert.InRange(bytes, 1, 900);
        hw.Delay = true;
        Invoke(sender, "MotionDto", segment);
        Assert.Equal(bytes, hw.Pending.Length);
        source[19].Id = 99;
        Assert.Equal(42, Count(sender)); Assert.Equal(0, Count(receiver));
        hw.Flush();
        Assert.Equal(42, Count(receiver));
        Console.WriteLine($"30 unmanaged poses: MemoryPack frame {bytes} bytes. One pooled frame, no claim of zero managed allocations.");
    }
    [Fact]
    public void Woven_reference_dto_uses_generated_memorypack_formatter_not_json()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "dto", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "dto", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var h = Activator.CreateInstance(Woven.Value)!;
        var receiver = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, h, _ => true); client.Bind(key, receiver);
        var dto = new CodableDto { Id = 54 };
        var method = Woven.Value.GetMethod("DtoMotion")!;
        var expected = host.MeasureUnreliableCall(key, method, new object?[] { dto });
        Invoke(h, "DtoMotion", dto);
        Assert.Equal(54, Count(receiver)); Assert.Equal(0, hw.ReliableCalls);
        Assert.InRange(expected, 1, 900);
    }

    private sealed class ReliableOnlyWire : IRoomWire
    {
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received { add { } remove { } }
        public event Action<PeerId>? PeerLeft { add { } remove { } }
        public Task SendAsync(PeerId peer, byte[] bytes) => throw new InvalidOperationException("No network needed");
        public void Dispose() { }
    }

    [Fact]
    public void Arrival_lane_spoofing_is_rejected_without_reply()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "spoof", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "spoof", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var h = Activator.CreateInstance(Woven.Value)!;
        var a = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, h, _ => true); client.Bind(key, a);
        var errors = new List<Exception>(); client.UnhandledDispatch += errors.Add;
        var method = Woven.Value.GetMethod("Motion")!;
        var forged = new Packet { Kind = "call", Scope = "spoof", Id = "attack", Service = key.Service,
            Method = RpcRuntime.MethodId(method), To = SendTo.All, Origin = "host", Destination = "a",
            Args = new[] { ReliableValues.Encode(typeof(ArraySegment<int>), new ArraySegment<int>(new[] { 99 })) } };
        aw.InjectReliable(new PeerId("host"), ReliableCodec.Encode(forged));
        Assert.Equal(0, Count(a)); Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.InvalidPayload);
        Assert.Equal(0, aw.ReliableCalls);
        var bytes = UnreliableFrame("spoof", key, method, SendTo.All, "", "host", new ArraySegment<int>(new[] { 17 }));
        aw.Inject(new PeerId("host"), bytes);
        Assert.True(Count(a) == 17, string.Join(" | ", errors.Select(e => e.ToString())));
        Assert.Equal(17, Count(a));
        Assert.Equal(0, aw.ReliableCalls);
        hw.Delay = true;
        Invoke(h, "Motion", new ArraySegment<int>(new[] { 1 }));
        var typed = hw.Pending.ToArray();
        Assert.Equal(TypedRpcHeader.Magic, typed[0]);
        aw.InjectReliable(new PeerId("host"), typed);
        var spoofedKind = typed.ToArray(); spoofedKind[2] = 1;
        aw.Inject(new PeerId("host"), spoofedKind);
        Assert.Equal(17, Count(a));
        Assert.True(errors.Count(e => e is RpcException r && r.Error == RpcError.InvalidPayload) >= 3);
        hw.Flush();
        Assert.Equal(1, Count(a));
        hw.Delay = false;
        var hostErrors = new List<Exception>(); host.UnhandledDispatch += hostErrors.Add;
        var reliable = Woven.Value.GetMethod("Add")!;
        var wrongLane = UnreliableFrame("spoof", key, reliable, SendTo.Host, "host", "a", 4);
        hw.Inject(new PeerId("a"), wrongLane);
        Assert.Contains(hostErrors, e => e is RpcException r && r.Error == RpcError.InvalidPayload);
        Assert.Equal(0, hw.ReliableCalls);
        var hostile = UnreliableFrame("spoof", key, method, SendTo.All, "", "host", null, hostileCollection: true);
        Assert.InRange(hostile.Length, 1, 900);
        aw.Inject(new PeerId("host"), hostile);
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.LimitExceeded);
        Assert.Equal(1, Count(a));
        Console.WriteLine($"MemoryPack unreliable payload {bytes.Length} bytes. Object[] and reflection remain (not zero-GC).");
    }
    [Fact]
    public void Host_and_target_relay_preserve_real_sender_without_reliable_calls()
    {
        var hw = new Wire(); var aw = new Wire(); var bw = new Wire(); Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = new RpcRuntime(NetworkRole.Host, "routes", new PeerId("host"), new PeerId("host"), hw);
        using var a = new RpcRuntime(NetworkRole.Client, "routes", new PeerId("a"), new PeerId("host"), aw);
        using var b = new RpcRuntime(NetworkRole.Client, "routes", new PeerId("b"), new PeerId("host"), bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("routes"); var h = Activator.CreateInstance(Woven.Value)!;
        var source = Activator.CreateInstance(Woven.Value)!; var receiver = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, h, _ => true); a.Bind(key, source); b.Bind(key, receiver);
        Invoke(source, "HostMotion", 9);
        Assert.Equal(9, Count(h)); Assert.Equal("a", Woven.Value.GetField("Sender")!.GetValue(h));
        Woven.Value.GetMethod("TargetMotion")!.Invoke(source, new object[] { new RpcTarget(new PeerId("b")), 23 });
        Assert.Equal(23, Count(receiver)); Assert.Equal("a", Woven.Value.GetField("Sender")!.GetValue(receiver));
        Assert.Equal(0, hw.ReliableCalls + aw.ReliableCalls + bw.ReliableCalls);
        Assert.Equal(2, aw.DatagramCalls); Assert.Equal(1, hw.DatagramCalls);
    }
    [Fact]
    public async Task Failed_datagram_send_is_observed_once_without_fallback_or_reply()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "fault", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "fault", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var h = Activator.CreateInstance(Woven.Value)!;
        var receiver = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, h, _ => true); client.Bind(key, receiver);
        var pool = Track(host);
        var activeBefore = TypedPacketBuffer.ActiveRentals;
        var method = Woven.Value.GetMethod("Motion")!;
        Assert.True(host.MeasureUnreliableCall(key, method, new object?[] { new ArraySegment<int>(new[] { 4 }, 0, 1) }) > 0);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(0, Leases(host)); Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
        var errors = new List<Exception>(); host.UnhandledDispatch += errors.Add;
        hw.Fault = true;
        Invoke(h, "Motion", new ArraySegment<int>(new[] { 1 }, 0, 1));
        for (int n = 0; n < 100 && errors.Count == 0; n++) await Task.Delay(10);
        Assert.Single(errors); Assert.Equal(1, hw.DatagramCalls);
        Assert.Equal(0, hw.ReliableCalls + aw.ReliableCalls);
        Assert.Equal(0, Count(receiver));
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns); Assert.Equal(0, Leases(host));
        Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, PendingReplies(host));
    }
    [Fact]
    public async Task Single_pool_lease_covers_every_delayed_fanout_send_and_all_failure_paths()
    {
        var hw = new Wire(); var aw = new Wire(); var bw = new Wire(); Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = new RpcRuntime(NetworkRole.Host, "lease", new PeerId("host"), new PeerId("host"), hw);
        using var a = new RpcRuntime(NetworkRole.Client, "lease", new PeerId("a"), new PeerId("host"), aw);
        using var b = new RpcRuntime(NetworkRole.Client, "lease", new PeerId("b"), new PeerId("host"), bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
        var first = Activator.CreateInstance(Woven.Value)!; var second = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => true); a.Bind(key, first); b.Bind(key, second);
        var pool = Track(host);
        var activeBefore = TypedPacketBuffer.ActiveRentals;
        var source = new int[128]; Array.Fill(source, -99); source[71] = 65;
        hw.Delay = true;
        Invoke(sender, "Motion", new ArraySegment<int>(source, 71, 1));
        Assert.Equal(2, hw.PendingCount); Assert.Equal(activeBefore + 1, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(0, Leases(host));
        Assert.Equal(0, PendingReplies(host));
        Assert.True(MemoryMarshal.TryGetArray(hw.PendingFor("a"), out var firstSlice));
        Assert.True(MemoryMarshal.TryGetArray(hw.PendingFor("b"), out var secondSlice));
        Assert.Same(firstSlice.Array, secondSlice.Array); Assert.Equal(firstSlice.Offset, secondSlice.Offset);
        var frozen = hw.PendingFor("b").ToArray();
        source[71] = 999;
        hw.Flush("a");
        Assert.Equal(65, Count(first)); Assert.Equal(0, Count(second));
        Assert.Equal(activeBefore + 1, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(frozen, hw.PendingFor("b").ToArray());
        hw.Flush("b");
        for (int n = 0; n < 100 && TypedPacketBuffer.ActiveRentals != activeBefore; n++) await Task.Delay(10);
        Assert.Equal(65, Count(second)); Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(0, Leases(host));
        Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, PendingReplies(host));
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => Invoke(sender, "Announce", new string('x', 1000))).Error);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, Leases(host));
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() =>
            host.MeasureUnreliableCall(key, Woven.Value.GetMethod("Announce")!, new object?[] { new string('y', 65500) })).Error);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns);
        var inflight = typeof(RpcRuntime).GetField("_typedInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inflight.SetValue(host, 128); hw.Delay = false;
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => Invoke(sender, "Motion", new ArraySegment<int>(source, 71, 1))).Error);
        inflight.SetValue(host, 0);
        Assert.Equal(65, Count(sender)); // Full backlog must not execute the local All body.
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, Leases(host)); Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public void Local_authorization_failure_releases_packet_without_starting_fanout()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "deny", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "deny", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => false); client.Bind(key, Activator.CreateInstance(Woven.Value)!);
        var pool = Track(host);
        Assert.Equal(RpcError.Unauthorized, Assert.Throws<RpcException>(() => Invoke(sender, "Motion", new ArraySegment<int>(new[] { 8 }, 0, 1))).Error);
        Assert.Equal(0, hw.DatagramCalls); Assert.Equal(0, Count(sender));
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, Leases(host)); Assert.Equal(0, PendingReplies(host));
    }
    [Fact]
    public async Task Disposal_does_not_return_a_packet_still_borrowed_by_an_inflight_wire_send()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "dispose", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "dispose", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => true); client.Bind(key, Activator.CreateInstance(Woven.Value)!);
        var pool = Track(host); hw.Delay = true;
        var activeBefore = TypedPacketBuffer.ActiveRentals;
        Invoke(sender, "Motion", new ArraySegment<int>(new[] { 7 }, 0, 1));
        Assert.Equal(activeBefore + 1, TypedPacketBuffer.ActiveRentals);
        var packetSnapshot = hw.Pending.ToArray();
        host.Dispose();
        Assert.Equal(activeBefore + 1, TypedPacketBuffer.ActiveRentals); Assert.Equal(packetSnapshot, hw.Pending.ToArray());
        hw.Flush();
        for (int n = 0; n < 100 && TypedPacketBuffer.ActiveRentals != activeBefore; n++) await Task.Delay(10);
        Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(pool.Rents, pool.Returns);
        Assert.Equal(0, Leases(host));
    }

    // Opt-in, process-isolated diagnostics: BITKIT_GC_REPORT=1 dotnet test ... --filter FullyQualifiedName~Gc_report
    // Full collections are intentionally restricted to this test. Never force GC in production.
    [Fact]
    public void Gc_report_warmed_woven_unreliable_memorypack_waves()
    {
        if (Environment.GetEnvironmentVariable("BITKIT_GC_REPORT") != "1") return;
        const int waves = 3, sendsPerWave = 1500, warmup = 320;
        var results = new List<object>();
        foreach (var (name, peers, poses, deliver) in new[]
        {
            ("sender_only_segment1", 1, false, false),
            ("end_to_end_segment1", 1, false, true),
            ("end_to_end_pose30", 1, true, true),
            ("fanout2_pose30", 2, true, true),
            ("fanout8_pose30", 8, true, true)
        })
        {
            var hw = new Wire { Deliver = deliver };
            var clients = new List<RpcRuntime>();
            using var host = new RpcRuntime(NetworkRole.Host, "gc-report", new PeerId("host"), new PeerId("host"), hw);
            try
            {
                for (int i = 0; i < peers; i++)
                {
                    var id = "peer" + i;
                    var wire = new Wire(); Link(hw, wire, id);
                    var client = new RpcRuntime(NetworkRole.Client, "gc-report", new PeerId(id), new PeerId("host"), wire);
                    clients.Add(client);
                    host.RegisterMember(new RoomMember(new PeerId(id)));
                    client.ConfirmReady();
                }
                var key = new TargetKey("motion");
                var sender = Activator.CreateInstance(Woven.Value)!;
                host.Bind(key, sender, _ => true);
                foreach (var client in clients) client.Bind(key, Activator.CreateInstance(Woven.Value)!);
                var pool = Track(host);
                var baselineTyped = TypedPacketBuffer.ActiveRentals;
                var values = new int[128]; values[19] = 42;
                var poseArray = new MotionPayload[128];
                for (int i = 0; i < poseArray.Length; i++)
                    poseArray[i] = new MotionPayload { Id = i, X = i * 0.1f, Z = -i * 0.2f, AimX = i * 0.01f, AimZ = 0.33f };
                var method = Woven.Value.GetMethod(poses ? "MotionDto" : "Motion")!;
                var invocation = new object?[] { poses ? new ArraySegment<MotionPayload>(poseArray, 19, 30) : new ArraySegment<int>(values, 19, 1) };
                var payload = host.MeasureUnreliableCall(key, method, invocation);
                Assert.InRange(payload, 1, 900);
                for (int n = 0; n < warmup; n++) method.Invoke(sender, invocation);
                Assert.Equal(0, Leases(host)); Assert.Equal(0, pool.Outstanding);
                Assert.Equal(baselineTyped, TypedPacketBuffer.ActiveRentals);
                var sample = new List<object>();
                for (int wave = 0; wave < waves; wave++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var retainedBefore = GC.GetTotalMemory(false);
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var totalBefore = GC.GetTotalAllocatedBytes(precise: true);
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    for (int n = 0; n < sendsPerWave; n++) method.Invoke(sender, invocation);
                    long currentThread = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    long total = GC.GetTotalAllocatedBytes(precise: true) - totalBefore;
                    var gen0 = GC.CollectionCount(0) - g0; var gen1 = GC.CollectionCount(1) - g1; var gen2 = GC.CollectionCount(2) - g2;
                    Assert.Equal(0, Leases(host)); Assert.Equal(0, pool.Outstanding);
                    Assert.Equal(pool.Rents, pool.Returns);
                    Assert.Equal(baselineTyped, TypedPacketBuffer.ActiveRentals);
                    Assert.Equal(0, PendingReplies(host));
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var retainedAfter = GC.GetTotalMemory(false);
                    sample.Add(new { wave, sends = sendsPerWave, currentThreadBytes = currentThread, totalProcessBytes = total,
                        perSendThreadBytes = (double)currentThread / sendsPerWave, perSendTotalBytes = (double)total / sendsPerWave,
                        gen0, gen1, gen2, retainedBefore, retainedAfter, retainedDelta = retainedAfter - retainedBefore,
                        typedActiveLeases = TypedPacketBuffer.ActiveRentals - baselineTyped,
                        typedPeakLeasesProcessWide = TypedPacketBuffer.PeakRentals,
                        legacyV2ActiveLeases = Leases(host), pendingReplies = PendingReplies(host),
                        legacyV2PoolOutstanding = pool.Outstanding, legacyV2PoolRents = pool.Rents, legacyV2PoolReturns = pool.Returns });
                }
                results.Add(new { scenario = name, peers, deliver, payloadBytes = payload, warmup, waves = sample });
            }
            finally { foreach (var client in clients) client.Dispose(); }
        }
        object lifecycle;
        {
            var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
            using var host = new RpcRuntime(NetworkRole.Host, "gc-lifecycle", new PeerId("host"), new PeerId("host"), hw);
            using var client = new RpcRuntime(NetworkRole.Client, "gc-lifecycle", new PeerId("a"), new PeerId("host"), aw);
            host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
            var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
            host.Bind(key, sender, _ => true); client.Bind(key, Activator.CreateInstance(Woven.Value)!);
            var pool = Track(host);
            var baselineTyped = TypedPacketBuffer.ActiveRentals;
            var argument = new ArraySegment<int>(new[] { 1 });
            hw.Fault = true; Invoke(sender, "Motion", argument);
            Assert.True(SpinWait.SpinUntil(() => TypedPacketBuffer.ActiveRentals == baselineTyped, TimeSpan.FromSeconds(2)));
            int faultOutstanding = TypedPacketBuffer.ActiveRentals - baselineTyped;
            hw.Fault = false; hw.Delay = true; Invoke(sender, "Motion", argument);
            int delayedOutstanding = TypedPacketBuffer.ActiveRentals - baselineTyped;
            host.Dispose();
            int disposedOutstanding = TypedPacketBuffer.ActiveRentals - baselineTyped;
            hw.CancelPending();
            Assert.True(SpinWait.SpinUntil(() => TypedPacketBuffer.ActiveRentals == baselineTyped, TimeSpan.FromSeconds(2)));
            lifecycle = new { faultOutstanding, delayedOutstanding, disposedOutstanding,
                afterCancelledWireTaskOutstanding = TypedPacketBuffer.ActiveRentals - baselineTyped,
                typedPeakLeasesProcessWide = TypedPacketBuffer.PeakRentals, pendingReplies = PendingReplies(host),
                legacyV2PoolRents = pool.Rents, legacyV2PoolReturns = pool.Returns };
            Assert.Equal(pool.Rents, pool.Returns);
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var output = Path.Combine(root!.FullName, "Artifacts", "GcRuns");
        Directory.CreateDirectory(output);
        var stamp = DateTime.UtcNow;
        var filename = Path.Combine(output, "v4-typed-unreliable-memorypack-" + stamp.ToString("yyyyMMdd-HHmmss") + ".json");
        var report = new { utc = stamp.ToString("O"), revision = "unborn HEAD (workspace files untracked)",
            coreModuleVersionId = typeof(RpcRuntime).Assembly.ManifestModule.ModuleVersionId,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            note = "V4 typed B6 over fake UDP; synchronous current-thread sample includes MethodInfo.Invoke harness and Host local body; process-total includes test runner/background. LegacyV2 pool values are compatibility counters only.",
            wavesPerScenario = waves, sendsPerWave, warmup, scenarios = results, lifecycle };
        File.WriteAllText(filename, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("V4 GC evidence: " + filename);
    }
    [Fact]
    public async Task Delayed_wire_cancellation_and_real_128_send_cap_release_all_rentals()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "bounded", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "bounded", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("motion"); var sender = Activator.CreateInstance(Woven.Value)!;
        host.Bind(key, sender, _ => true); client.Bind(key, Activator.CreateInstance(Woven.Value)!);
        var pool = Track(host); hw.Delay = true;
        var activeBefore = TypedPacketBuffer.ActiveRentals;
        var segment = new ArraySegment<int>(new[] { 1 });
        for (int i = 0; i < 128; i++) Invoke(sender, "Motion", segment);
        Assert.Equal(128, hw.PendingCount);
        Assert.Equal(activeBefore + 128, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, pool.Outstanding); Assert.Equal(0, Leases(host));
        Assert.Equal(0, PendingReplies(host));
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => Invoke(sender, "Motion", segment)).Error);
        Assert.Equal(activeBefore + 128, TypedPacketBuffer.ActiveRentals); Assert.Equal(128, hw.DatagramCalls);
        hw.CancelPending();
        for (int i = 0; i < 127; i++) hw.Flush();
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != activeBefore; i++) await Task.Delay(10);
        Assert.Equal(0, Leases(host)); Assert.Equal(0, pool.Outstanding);
        Assert.Equal(activeBefore, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(pool.Rents, pool.Returns); Assert.Equal(0, PendingReplies(host));
    }
}
