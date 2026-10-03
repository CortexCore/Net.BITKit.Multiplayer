using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using BITKit.Multiplayer;
using BITKit.Multiplayer.CodeGen;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace BITKit.Multiplayer.Tests;

[Collection("Runtime defaults isolation")]
public sealed class TypedHotTests
{
    private static Type Probe => RuntimeTests.WovenAssembly.GetType("Fixture.HotProbe")!;
    private static object NewProbe() => Activator.CreateInstance(Probe)!;
    private sealed class Wire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
    {
        internal readonly Dictionary<PeerId, (Wire Wire, PeerId Sender)> Links = new();
        private readonly List<(PeerId Peer, ReadOnlyMemory<byte> Data, TaskCompletionSource Completion, bool Datagram)> _held = new();
        internal bool Delay;
        internal bool Deliver = true;
        internal bool Capture;
        public bool UnreliableEnabled { get; set; } = true;
        public int MaxUnreliablePayloadBytes => 900;
        public bool IsUnreliableReady(PeerId peer) => Links.ContainsKey(peer);
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public DatagramStatistics GetDatagramStatistics() => new();
        public Task RebindDatagramsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        internal int TypedSent, Replies;
        internal byte[]? LastTyped;
        internal int HeldCount => _held.Count;
        internal ReadOnlyMemory<byte> HeldPayload => _held[0].Data;
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received; // Runtime MUST NOT subscribe when MemoryReceived exists.
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft;
        public Task SendAsync(PeerId peer, byte[] data)
        {
            var link = Links[peer];
            if (data[0] == 0xB5 && ReliableCodec.Decode(data).Kind == "reply") Replies++;
            link.Wire.MemoryReceived?.Invoke(link.Sender, data);
            return Task.CompletedTask;
        }
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            TypedSent++;
            if (Capture) LastTyped = payload.ToArray(); // opt-in test-only capture, never benchmark path
            if (Delay)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add((peer, payload, completion, false));
                return new ValueTask(completion.Task);
            }
            if (!Deliver) return ValueTask.CompletedTask;
            var link = Links[peer]; link.Wire.MemoryReceived?.Invoke(link.Sender, payload);
            return ValueTask.CompletedTask;
        }
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload)
        {
            TypedSent++;
            if (Capture) LastTyped = payload.ToArray();
            if (Delay)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add((peer, payload, completion, true));
                return completion.Task;
            }
            if (Deliver)
            {
                var link = Links[peer]; link.Wire.UnreliableReceived?.Invoke(link.Sender, payload);
            }
            return Task.CompletedTask;
        }
        internal void Complete()
        {
            var pending = _held[0]; _held.RemoveAt(0);
            var link = Links[pending.Peer];
            if (pending.Datagram) link.Wire.UnreliableReceived?.Invoke(link.Sender, pending.Data);
            else link.Wire.MemoryReceived?.Invoke(link.Sender, pending.Data);
            pending.Completion.SetResult();
        }
        internal void Fault()
        { var pending = _held[0]; _held.RemoveAt(0); pending.Completion.SetException(new IOException("fake delayed fault")); }
        internal void Inject(PeerId physicalSender, byte[] data) => MemoryReceived?.Invoke(physicalSender, data);
        internal void Disconnect(PeerId peer) => PeerLeft?.Invoke(peer);
        internal void InjectDuplicateLegacy(PeerId physicalSender, byte[] data) => Received?.Invoke(physicalSender, data);
        public void Dispose() { }
    }
    private static void Link(Wire host, Wire client, string peer)
    {
        host.Links[new PeerId(peer)] = (client, new PeerId("host"));
        client.Links[new PeerId("host")] = (host, new PeerId(peer));
    }
    private static int Count(object instance) => (int)Probe.GetField("Count")!.GetValue(instance)!;

    [Fact]
    public void Numeric_target_identity_is_unambiguous_with_embedded_nuls_and_scope_rejects_invalid_unicode()
    {
        var first = new TargetKey("a\0b", "c", "d");
        var second = new TargetKey("a", "b\0c", "d");
        Assert.NotEqual(first, second);
        Assert.NotEqual(TypedTarget.Hash(first), TypedTarget.Hash(second));
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "nul-targets", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "nul-targets", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var hFirst = NewProbe(); var hSecond = NewProbe(); var cFirst = NewProbe(); var cSecond = NewProbe();
        host.Bind(first, hFirst, owner: new PeerId("a")); host.Bind(second, hSecond, owner: new PeerId("a"));
        client.Bind(first, cFirst); client.Bind(second, cSecond);
        Probe.GetMethod("Tick")!.CreateDelegate<Action<int>>(cFirst)(3);
        Probe.GetMethod("Tick")!.CreateDelegate<Action<int>>(cSecond)(7);
        Assert.Equal(3, Count(hFirst)); Assert.Equal(7, Count(hSecond));
        Assert.Equal(RpcError.InvalidPayload, Assert.Throws<RpcException>(() => TypedTarget.Hash(new TargetKey("\ud800"))).Error);
        Assert.Equal(RpcError.InvalidPayload, Assert.Throws<RpcException>(() => TypedTarget.Scope("bad\ud800room")).Error);
        Assert.Throws<System.Text.EncoderFallbackException>(() => ReliableCodec.Encode(new Packet { Kind = "membersRequest", Scope = "bad\ud800room" }));
    }

    [Fact]
    public void Cecil_emits_typed_nonboxing_wrapper_and_direct_body_receiver()
    {
        using var assembly = ModuleDefinition.ReadModule(RuntimeTests.WovenAssembly.Location);
        var type = assembly.Types.Single(t => t.Name == "HotProbe");
        foreach (var name in new[] { "Tick", "Scalar", "Poses", "Echo", "Reenter", "Broadcast", "Target", "UdpTick", "UdpScalar", "UdpBroadcast", "AsyncParent", "SyncNested", "AsyncChild", "SyncThrows" })
        {
            var method = type.Methods.Single(m => m.Name == name);
            Assert.Contains(method.CustomAttributes, a => a.AttributeType.FullName == typeof(WovenTypedRpcAttribute).FullName);
            Assert.DoesNotContain(method.Body.Instructions, i => i.OpCode == OpCodes.Box || i.OpCode == OpCodes.Newarr ||
                i.Operand is MethodReference reference && reference.Name == "Invoke");
            var receiver = type.Methods.Single(m => m.Name.StartsWith("__bitkit_recv_" + name + "_", StringComparison.Ordinal));
            Assert.Contains(receiver.Body.Instructions, i => i.Operand is MethodReference reference && reference.Name.StartsWith("__bitkit_body_" + name + "_", StringComparison.Ordinal));
            Assert.DoesNotContain(receiver.Body.Instructions, i => i.OpCode == OpCodes.Box || i.OpCode == OpCodes.Newarr ||
                i.Operand is MethodReference reference && reference.Name == "Invoke");
        }
    }
    [Fact]
    public async Task Woven_void_interface_and_concrete_calls_use_borrowed_memory_without_reply_or_waiter()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var authority = NewProbe(); var caller = NewProbe();
        host.Bind(key, authority, owner: new PeerId("a")); client.Bind(key, caller);
        var interfaceCaller = (Fixture.IHotProbe)caller;
        interfaceCaller.Tick(3);
        Probe.GetMethod("Tick")!.Invoke(caller, new object?[] { 4 });
        Assert.Equal(7, Count(authority)); Assert.Equal("a", Probe.GetField("LastSender")!.GetValue(authority));
        Assert.Equal(0, Count(caller));
        Assert.Equal(0, hw.Replies + aw.Replies);
        Assert.Equal(0, PrivateCount(client, "_pending")); Assert.Equal(0, PrivateCount(host, "_fanout"));
        var call = interfaceCaller.Echo(8);
        Assert.Equal(9, await call);
        Assert.True(hw.Replies > 0); // Task<T> still has application completion.
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
    }
    private static int PrivateCount(RpcRuntime runtime, string field)
    {
        var value = typeof(RpcRuntime).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }
    [Fact]
    public async Task Borrowed_delayed_segment_freezes_prefix_and_pool_returns_on_completion_fault_disposal()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-lease", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-lease", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var authority = NewProbe(); var caller = NewProbe();
        host.Bind(key, authority, owner: new PeerId("a")); client.Bind(key, caller);
        var values = new Fixture.AnnotatedScalar[80];
        values[35] = new Fixture.AnnotatedScalar { Value = 13 };
        var startLeases = TypedPacketBuffer.ActiveRentals;
        aw.Delay = true;
        ((Fixture.IHotProbe)caller).Poses(new ArraySegment<Fixture.AnnotatedScalar>(values, 35, 1));
        Assert.Equal(1, aw.HeldCount); Assert.Equal(startLeases + 1, TypedPacketBuffer.ActiveRentals);
        var snapshot = aw.HeldPayload.ToArray(); values[35].Value = 99;
        Assert.Equal(snapshot, aw.HeldPayload.ToArray());
        aw.Complete();
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != startLeases; i++) await Task.Delay(10);
        Assert.Equal(13, Count(authority)); Assert.Equal(startLeases, TypedPacketBuffer.ActiveRentals);
        ((Fixture.IHotProbe)caller).Tick(4);
        aw.Fault();
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != startLeases; i++) await Task.Delay(10);
        Assert.Equal(startLeases, TypedPacketBuffer.ActiveRentals);
        ((Fixture.IHotProbe)caller).Tick(5);
        client.Dispose();
        Assert.Equal(startLeases + 1, TypedPacketBuffer.ActiveRentals); // cannot recycle while wire still borrows it
        aw.Fault();
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != startLeases; i++) await Task.Delay(10);
        Assert.Equal(startLeases, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public void Fingerprint_mismatch_is_rejected_before_receiver_body_and_has_no_void_reply()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-schema", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-schema", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var authority = NewProbe(); var caller = NewProbe();
        host.Bind(key, authority, owner: new PeerId("a")); client.Bind(key, caller);
        var errors = new ConcurrentQueue<Exception>(); host.UnhandledDispatch += errors.Enqueue;
        aw.Capture = true;
        ((Fixture.IHotProbe)caller).Tick(1);
        hw.InjectDuplicateLegacy(new PeerId("a"), aw.LastTyped!);
        Assert.Equal(1, Count(authority)); // MemoryReceived is subscribed instead of byte[] Received.
        var altered = aw.LastTyped!.ToArray(); altered[60] ^= 0x20;
        hw.Inject(new PeerId("a"), altered);
        Assert.Equal(1, Count(authority));
        Assert.Contains(errors, e => e is RpcException r && r.Error == RpcError.InvalidPayload);
        Assert.Equal(0, hw.Replies);
    }
    [Fact]
    public async Task Typed_task_schema_mismatch_returns_remote_error_and_releases_held_send()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-task-schema", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-task-schema", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var authority = NewProbe(); var caller = NewProbe();
        host.Bind(key, authority, owner: new PeerId("a")); client.Bind(key, caller);
        var baseline = TypedPacketBuffer.ActiveRentals;
        aw.Delay = true;
        var task = ((Fixture.IHotProbe)caller).Echo(17);
        Assert.Equal(1, aw.HeldCount); Assert.Equal(baseline + 1, TypedPacketBuffer.ActiveRentals);
        Assert.True(MemoryMarshal.TryGetArray(aw.HeldPayload, out var slice));
        slice.Array![slice.Offset + 60] ^= 0x01;
        aw.Complete();
        Assert.Equal(RpcError.InvalidPayload, (await Assert.ThrowsAsync<RpcException>(() => task)).Error);
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != baseline; i++) await Task.Delay(10);
        Assert.Equal(baseline, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public async Task Typed_oneway_inflight_cap_rejects_129th_without_waiter_or_pool_leak()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-backlog", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-backlog", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); host.Bind(key, NewProbe(), owner: new PeerId("a"));
        var sender = NewProbe(); client.Bind(key, sender);
        var before = TypedPacketBuffer.ActiveRentals;
        aw.Delay = true;
        var calls = (Fixture.IHotProbe)sender;
        for (int i = 0; i < 128; i++) calls.Tick(1);
        Assert.Equal(128, aw.HeldCount); Assert.Equal(before + 128, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => calls.Tick(1)).Error);
        Assert.Equal(before + 128, TypedPacketBuffer.ActiveRentals);
        Assert.Equal(0, PrivateCount(client, "_pending")); Assert.Equal(0, PrivateCount(host, "_fanout"));
        for (int i = 0; i < 128; i++) aw.Fault();
        for (int i = 0; i < 100 && TypedPacketBuffer.ActiveRentals != before; i++) await Task.Delay(10);
        Assert.Equal(before, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public async Task Nested_woven_async_sync_async_contexts_restore_parent_after_yield_and_exception()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "nested-context", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "nested-context", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var outer = NewProbe(); var middle = NewProbe(); var child = NewProbe(); var caller = NewProbe();
        Probe.GetField("Nested")!.SetValue(outer, middle);
        Probe.GetField("Nested")!.SetValue(middle, child);
        host.Bind(new TargetKey("outer"), outer, owner: new PeerId("a"));
        host.Bind(new TargetKey("middle"), middle, owner: new PeerId("a"));
        host.Bind(new TargetKey("child"), child, owner: new PeerId("a"));
        client.Bind(new TargetKey("outer"), caller);
        var send = Probe.GetMethod("AsyncParent")!.CreateDelegate<Func<Task<int>>>(caller);
        Assert.Equal(42, await send());
        Assert.Equal("a:outer", Probe.GetField("OuterBefore")!.GetValue(outer));
        Assert.Equal("host:middle", Probe.GetField("SyncObserved")!.GetValue(middle));
        Assert.Equal("host:child", Probe.GetField("ChildBefore")!.GetValue(child));
        Assert.Equal("host:child", Probe.GetField("ChildAfter")!.GetValue(child));
        Assert.Equal("host:middle", Probe.GetField("ThrowObserved")!.GetValue(middle));
        Assert.Equal("a:outer", Probe.GetField("OuterAfter")!.GetValue(outer));
        Assert.Null(RpcCallContext.Current);
        Assert.False(RpcCallContext.TryGetValue(out _));
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
    }
    [Fact]
    public void Woven_udp_scalar_and_fixed_struct_use_v4_and_exact_measure_with_warm_allocations()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-udp", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-udp", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var receiver = NewProbe(); var caller = NewProbe();
        host.Bind(key, receiver, owner: new PeerId("a")); client.Bind(key, caller);
        var tickMethod = Probe.GetMethod("UdpTick")!;
        var tick = tickMethod.CreateDelegate<Action<int>>(caller);
        var scalar = Probe.GetMethod("UdpScalar")!.CreateDelegate<Action<Fixture.AnnotatedScalar>>(caller);
        var one = new Fixture.AnnotatedScalar { Value = 1 };
        for (int i = 0; i < 256; i++) { tick(1); scalar(one); }
        aw.Capture = true;
        var measured = client.MeasureUnreliableCall(key, tickMethod, new object?[] { 1 });
        tick(1);
        Assert.Equal(TypedRpcHeader.Magic, aw.LastTyped![0]);
        Assert.Equal(3, aw.LastTyped[2]); // unreliable notification, not B4 compatibility traffic
        Assert.Equal(measured, aw.LastTyped.Length);
        aw.Capture = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) tick(1);
        var intBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) scalar(one);
        var structBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, intBytes);
        Assert.Equal(0L, structBytes);
        Assert.Equal(4513, Count(receiver));
        Assert.Equal(0, hw.Replies + aw.Replies);
        Assert.Equal(0, PrivateCount(client, "_pending")); Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
        Console.WriteLine($"Woven v4 UDP void, 2000 warmed in-memory borrowed sends: scalar {intBytes / 2000.0:F2} B/call; fixed struct {structBytes / 2000.0:F2} B/call; no claims for native wire or array receivers.");
    }
    [Fact]
    public void Typed_unreliable_host_all_honors_hold_barrier_without_ack_or_waiter()
    {
        var hw = new Wire(); var aw = new Wire(); var bw = new Wire(); Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-udp-all", new PeerId("host"), new PeerId("host"), hw);
        using var a = new RpcRuntime(NetworkRole.Client, "typed-udp-all", new PeerId("a"), new PeerId("host"), aw);
        using var b = new RpcRuntime(NetworkRole.Client, "typed-udp-all", new PeerId("b"), new PeerId("host"), bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        a.ConfirmReady(); b.ConfirmReady();
        var key = new TargetKey("probe"); var sender = NewProbe(); var first = NewProbe(); var second = NewProbe();
        host.Bind(key, sender, owner: new PeerId("a")); a.Bind(key, first); b.Bind(key, second);
        var send = Probe.GetMethod("UdpBroadcast")!.CreateDelegate<Action<int>>(sender);
        host.HoldBroadcasts(new PeerId("b"));
        send(2);
        Assert.Equal(2, Count(sender)); Assert.Equal(2, Count(first)); Assert.Equal(0, Count(second));
        host.ReleaseBroadcasts(new PeerId("b"));
        for (int i = 0; i < 100; i++) send(1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) send(1);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, bytes);
        Assert.Equal(1102, Count(sender)); Assert.Equal(1102, Count(first)); Assert.Equal(1100, Count(second));
        Assert.Equal(0, hw.Replies + aw.Replies + bw.Replies);
        Assert.Equal(0, PrivateCount(host, "_fanout")); Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
        Console.WriteLine($"Typed v4 unreliable Host All to two ready peers, warmed synchronous fake UDP: {bytes / 1000.0:F2} B/call; no guarantee for async/native UDP.");
    }
    [Fact]
    public void Woven_scalar_void_warm_sender_receiver_local_and_reentrancy_allocations_are_reported()
    {
        var hw = new Wire(); var aw = new Wire(); Link(hw, aw, "a");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-gc", new PeerId("host"), new PeerId("host"), hw);
        using var client = new RpcRuntime(NetworkRole.Client, "typed-gc", new PeerId("a"), new PeerId("host"), aw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); client.ConfirmReady();
        var key = new TargetKey("probe"); var authority = NewProbe(); var caller = NewProbe();
        int policyCalls = 0;
        host.Bind(key, authority, request =>
        {
            Interlocked.Increment(ref policyCalls);
            return request.Sender.Peer.Value is "a" or "host";
        });
        client.Bind(key, caller);
        var typed = (Fixture.IHotProbe)caller;
        var reenter = Probe.GetMethod("Reenter")!;
        reenter.Invoke(caller, new object?[] { 3 });
        Assert.Equal(3, Count(authority));
        for (int i = 0; i < 300; i++) typed.Tick(1);
        var startRentals = TypedPacketBuffer.ActiveRentals;
        const int sends = 2000;
        aw.Deliver = false;
        for (int i = 0; i < 100; i++) typed.Tick(1);
        long beforeSender = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < sends; i++) typed.Tick(1);
        var senderOnly = GC.GetAllocatedBytesForCurrentThread() - beforeSender;
        aw.Deliver = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < sends; i++) typed.Tick(1);
        var total = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, senderOnly);
        Assert.Equal(0L, total);
        Assert.Equal(2303, Count(authority));
        Assert.Equal(0, hw.Replies + aw.Replies);
        Assert.Equal(0, PrivateCount(client, "_pending")); Assert.Equal(0, PrivateCount(host, "_fanout"));
        Assert.Equal(startRentals, TypedPacketBuffer.ActiveRentals);
        Assert.True(policyCalls >= sends);
        Console.WriteLine($"Woven typed int void, 2,000 warm calls: sender-only {senderOnly} bytes / {senderOnly / (double)sends:F2} B per call; sender+receiver synchronous memory wire {total} bytes / {total / (double)sends:F2} B per call; no Task reply, not a general zero-GC claim.");
    }
    [Fact]
    public async Task Woven_fixed_struct_void_local_host_fanout_and_concurrent_calls_preserve_ownership()
    {
        var hw = new Wire(); var aw = new Wire(); var bw = new Wire(); Link(hw, aw, "a"); Link(hw, bw, "b");
        using var host = new RpcRuntime(NetworkRole.Host, "typed-many", new PeerId("host"), new PeerId("host"), hw);
        using var clientA = new RpcRuntime(NetworkRole.Client, "typed-many", new PeerId("a"), new PeerId("host"), aw);
        using var clientB = new RpcRuntime(NetworkRole.Client, "typed-many", new PeerId("b"), new PeerId("host"), bw);
        host.RegisterMember(new RoomMember(new PeerId("a"))); host.RegisterMember(new RoomMember(new PeerId("b")));
        clientA.ConfirmReady(); clientB.ConfirmReady();
        var key = new TargetKey("probe"); var h = NewProbe(); var a = NewProbe(); var b = NewProbe();
        host.Bind(key, h, owner: new PeerId("a")); clientA.Bind(key, a); clientB.Bind(key, b);
        var scalarA = Probe.GetMethod("Scalar")!.CreateDelegate<Action<Fixture.AnnotatedScalar>>(a);
        var scalarHost = Probe.GetMethod("Scalar")!.CreateDelegate<Action<Fixture.AnnotatedScalar>>(h);
        var broadcast = Probe.GetMethod("Broadcast")!.CreateDelegate<Action<int>>(h);
        var value = new Fixture.AnnotatedScalar { Value = 1 };
        for (int i = 0; i < 256; i++) { scalarA(value); scalarHost(value); broadcast(1); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) scalarA(value);
        var remote = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) scalarHost(value);
        var local = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) broadcast(1);
        var fanout = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, remote);
        Assert.Equal(0L, local);
        Assert.Equal(0L, fanout);
        Assert.Equal(1000 + 256, Count(a)); Assert.Equal(1000 + 256, Count(b));
        Assert.Equal(3000 + 768, Count(h)); // remote scalar, local scalar and one Host All body
        Assert.Equal(0, PrivateCount(host, "_fanout")); Assert.Equal(0, PrivateCount(clientA, "_pending"));
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
        Console.WriteLine($"Typed fixed unmanaged scalar void / call, warmed synchronous fake borrowed wire: remote {remote} B, Host-local {local} B, fanout2+Host {fanout} B per 1,000 calls; native transport and async scheduling are separate measurements.");
        var send = (Fixture.IHotProbe)a;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 150; i++) send.Tick(1);
        })));
        Assert.Equal(4200 + 768, Count(h));
        Assert.Equal(0, TypedPacketBuffer.ActiveRentals);
    }
}
