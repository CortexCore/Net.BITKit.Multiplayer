using System.Buffers.Binary;
using System.Reflection;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public class SyncCollectionTests
{
    private static readonly PeerId HostId = new("host"), ClientId = new("client");
    private static readonly TargetKey Key = new("sync-collections");
    private static object New(string name = "SyncState") => Activator.CreateInstance(RuntimeTests.WovenAssembly.GetType("Fixture." + name)!)!;
    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name)!.GetValue(owner)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name)!.GetValue(owner)!;
    private static SyncDictionary<int, int> Slots(object owner) => Property<SyncDictionary<int, int>>(owner, "Slots");
    private static SyncList<string> Tasks(object owner) => Property<SyncList<string>>(owner, "Tasks");
    private static SyncHashSet<int> Unlocks(object owner) => Property<SyncHashSet<int>>(owner, "Unlocks");
    private static void Health(object owner, int value) => owner.GetType().GetProperty("Health")!.SetValue(owner, value);
    private static ulong Fingerprint(object owner, string name)
    {
        var members = owner.GetType().GetProperties().Where(p => p.IsDefined(typeof(SyncVarAttribute)))
            .ToDictionary(p => p.Name, p => p.GetCustomAttribute<WovenSyncVarAttribute>()!.Fingerprint);
        return SyncWire.Mix(members[name], SyncWire.ContractFingerprint(members));
    }
    private static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.True(condition()); }
    private sealed class Wire : IRoomWire
    {
        public Wire Other = null!;
        public PeerId Identity;
        public bool IsConnected => true;
        public readonly List<byte[]> Sent = new();
        public Func<Packet, bool>? Drop;
        public TaskCompletionSource<bool>? HoldDeltas;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft;
        public Task SendAsync(PeerId peer, byte[] bytes)
        {
            lock (Sent) Sent.Add(bytes);
            if (bytes[0] == 0xB5)
            {
                var packet = ReliableCodec.Decode(bytes);
                if (Drop?.Invoke(packet) == true) return Task.CompletedTask;
                if (packet.Kind == "stateDelta" && HoldDeltas != null) return HoldDeltas.Task;
            }
            Other.Received?.Invoke(Identity, bytes); return Task.CompletedTask;
        }
        public void Inject(PeerId sender, byte[] bytes) => Received?.Invoke(sender, bytes);
        public void Leave(PeerId peer) => PeerLeft?.Invoke(peer);
        public void Dispose() { }
        public Packet[] Packets(string kind)
        { lock (Sent) return Sent.Where(b => b[0] == 0xB5).Select(b => ReliableCodec.Decode(b)).Where(p => p.Kind == kind).ToArray(); }
    }
    private sealed class Pair : IDisposable
    {
        public readonly Wire HW = new() { Identity = HostId }, CW = new() { Identity = ClientId };
        public readonly RpcRuntime Host, Client;
        public readonly object Authority, Replica;
        public readonly List<Exception> Errors = new();
        public Pair(bool bindClient = true, string type = "SyncState")
        {
            HW.Other = CW; CW.Other = HW;
            Host = new(NetworkRole.Host, "sync-test", HostId, HostId, HW);
            Client = new(NetworkRole.Client, "sync-test", ClientId, HostId, CW);
            Host.UnhandledDispatch += Errors.Add; Client.UnhandledDispatch += Errors.Add;
            Host.RegisterMember(new RoomMember(ClientId)); Client.ConfirmReady();
            Authority = New(type); Replica = New(type);
            Host.Bind(Key, Authority, _ => true); if (bindClient) Client.Bind(Key, Replica);
        }
        public void Dispose() { Host.Dispose(); Client.Dispose(); }
    }

    [Fact]
    public void WovenCollectionsPreserveIdentityAuthorityHooksAndBatchOperations()
    {
        using var pair = new Pair();
        var host = pair.Authority; var client = pair.Replica;
        var same = Slots(client);
        Assert.Single(Field<List<SyncDictionaryChange<int, int>>>(client, "SlotEvents"));
        Assert.Equal(SyncOperation.Reset, Field<List<SyncDictionaryChange<int, int>>>(client, "SlotEvents")[0].Operation);
        Assert.Throws<RpcException>(() => Slots(client)[1] = 2);
        Assert.Throws<RpcException>(() => Tasks(client).Clear());
        Assert.Throws<RpcException>(() => Unlocks(client).Remove(99));
        Slots(host).Add(1, 2); Slots(host)[1] = 3; Slots(host)[1] = 3;
        Slots(host).SetRange(new[] { new KeyValuePair<int, int>(2, 4), new KeyValuePair<int, int>(3, 5) });
        Assert.Equal(3, Slots(client).Count); Assert.Equal(3, Slots(client)[1]);
        Assert.Same(same, Slots(client)); Assert.Equal(3, Slots(client).Version);
        Assert.True(Slots(host).Remove(2)); Assert.False(Slots(host).Remove(2)); Slots(host).Clear();
        Tasks(host).AddRange(new[] { "a", "b", "c" }); Tasks(host).Insert(1, "x"); Tasks(host)[0] = "z";
        Assert.True(Tasks(host).Remove("b")); Tasks(host).RemoveAt(2);
        Assert.Equal(new[] { "z", "x" }, Tasks(client).ToArray()); Tasks(host).Clear();
        Assert.True(Unlocks(host).Add(7)); Assert.False(Unlocks(host).Add(7));
        Unlocks(host).UnionWith(new[] { 7, 8, 9 }); Unlocks(host).Remove(8);
        Assert.Equal(new[] { 7, 9 }, Unlocks(client).OrderBy(x => x)); Unlocks(host).Clear();
        Health(host, 90); Health(host, 90);
        Assert.Equal(new[] { (100, 90) }, Field<List<(int, int)>>(host, "HealthEvents"));
        Assert.Equal(new[] { (100, 90) }, Field<List<(int, int)>>(client, "HealthEvents"));
        Assert.All(Field<List<SyncDictionaryChange<int, int>>>(host, "SlotEvents"), c => Assert.Equal(SyncChangeOrigin.Local, c.Origin));
        Assert.Empty(pair.Errors);
    }

    [Fact]
    public void InitialSnapshotLateBindDetachRestoreAndTombstone()
    {
        using var pair = new Pair(false);
        Slots(pair.Authority)[7] = 17; Tasks(pair.Authority).Add("late"); Unlocks(pair.Authority).Add(5);
        int ready = 0; pair.Client.Synchronized += _ => ready++;
        pair.Client.Bind(Key, pair.Replica);
        Assert.Equal(17, Slots(pair.Replica)[7]); Assert.Equal("late", Tasks(pair.Replica)[0]); Assert.True(Unlocks(pair.Replica).Contains(5));
        Assert.Equal(1, ready);
        pair.Host.Unbind(Key);
        Assert.Throws<RpcException>(() => Slots(pair.Authority)[1] = 1);
        var replacement = New(); pair.Host.Bind(Key, replacement, _ => true);
        Assert.Equal(17, Slots(replacement)[7]); Slots(replacement)[7] = 18;
        Assert.Equal(18, Slots(pair.Replica)[7]);
        pair.Host.RemoveTarget(Key);
        Assert.Throws<RpcException>(() => Slots(pair.Replica)[1] = 1);
        Assert.Throws<RpcException>(() => pair.Client.Bind(Key, New()));
        Assert.Empty(pair.Errors);
    }

    [Fact]
    public void LostDeltaResynchronizesAndReplayCannotDuplicateHooks()
    {
        using var pair = new Pair();
        bool drop = true;
        pair.HW.Drop = p => p.Kind == "stateDelta" && drop;
        Slots(pair.Authority)[1] = 10;
        drop = false; Slots(pair.Authority)[2] = 20;
        Assert.Equal(10, Slots(pair.Replica)[1]); Assert.Equal(20, Slots(pair.Replica)[2]);
        int notifications = Field<List<SyncDictionaryChange<int, int>>>(pair.Replica, "SlotEvents").Count;
        var delta = pair.HW.Packets("stateDelta")[0];
        pair.CW.Inject(HostId, ReliableCodec.Encode(delta));
        Assert.Equal(notifications, Field<List<SyncDictionaryChange<int, int>>>(pair.Replica, "SlotEvents").Count);
        Assert.Equal(2, Slots(pair.Replica).Version); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void WrongFingerprintMalformedBatchAndForgedSenderCannotMutate()
    {
        using var pair = new Pair();
        Slots(pair.Authority)[1] = 1;
        var packet = pair.HW.Packets("stateDelta").Last(); packet.Version++;
        packet.Value = packet.Value!.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(packet.Value.AsSpan(11), 1);
        packet.Value[3] ^= 1;
        pair.CW.Inject(HostId, ReliableCodec.Encode(packet));
        Assert.Contains(pair.Errors, e => e.Message.Contains("fingerprint")); pair.Errors.Clear();
        packet.Value[3] ^= 1;
        pair.CW.Inject(new PeerId("forged"), ReliableCodec.Encode(packet)); Assert.Empty(pair.Errors);
        // Two operations: one valid Add then a malformed opcode. Neither may commit.
        using var writer = new BinaryBufferWriter(100);
        SyncWire.Int(writer, 2); SyncWire.Byte(writer, (byte)SyncOperation.Add);
        SyncValue<int>.Write(writer, 2); SyncValue<int>.Write(writer, 2); SyncWire.Byte(writer, 255);
        packet.Value = SyncWire.Wrap(2, Fingerprint(pair.Replica, "Slots"), 1, writer.CopyOwned());
        pair.CW.Inject(HostId, ReliableCodec.Encode(packet));
        Assert.False(Slots(pair.Replica).ContainsKey(2)); Assert.Equal(1, Slots(pair.Replica).Version);
        Assert.NotEmpty(pair.Errors);
        pair.Errors.Clear(); Slots(pair.Authority)[2] = 22;
        Assert.Equal(22, Slots(pair.Replica)[2]); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void LimitsNoOpInitializationAndAliasingAreExplicit()
    {
        var prebind = New(); Slots(prebind)[1] = 2;
        Assert.Empty(Field<List<SyncDictionaryChange<int, int>>>(prebind, "SlotEvents"));
        using var pair = new Pair();
        Assert.Throws<InvalidOperationException>(() => pair.Host.Bind(new TargetKey("null"), New("NullSyncState"), _ => true));
        Assert.Throws<InvalidOperationException>(() => pair.Host.Bind(new TargetKey("shared"), New("SharedSyncState"), _ => true));
        Assert.Throws<RpcException>(() => Tasks(pair.Authority).AddRange(Enumerable.Repeat("x", 65)));
        Assert.Empty(Tasks(pair.Authority));
        Assert.Throws<RpcException>(() => Tasks(pair.Authority).Add(new string('x', 20000)));
        Assert.Empty(Tasks(pair.Authority)); Assert.Empty(Tasks(pair.Replica));
        for (int batch = 0; batch < 4; batch++) Unlocks(pair.Authority).UnionWith(Enumerable.Range(batch * 64, 64));
        Assert.Throws<RpcException>(() => Unlocks(pair.Authority).Add(300));
        Assert.Equal(256, Unlocks(pair.Replica).Count);
    }

    [Fact]
    public async Task BoundedWriterHoldsMemoryUntilCompletionAndDetachesQueuedWork()
    {
        using var pair = new Pair();
        pair.HW.HoldDeltas = new(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i < 128; i++) Slots(pair.Authority)[0] = i;
        Assert.Throws<RpcException>(() => Slots(pair.Authority)[0] = 999);
        Assert.Equal(127, Slots(pair.Authority)[0]);
        pair.Host.Unbind(Key); pair.HW.HoldDeltas.SetResult(true);
        await Until(() => pair.HW.Packets("stateDelta").Length == 1);
        Assert.Empty(Slots(pair.Replica));
    }

    [Fact]
    public void ThrowingHooksDoNotRollbackOrHoldLocksAndReentrancyIsDiagnosed()
    {
        using var pair = new Pair();
        pair.Replica.GetType().GetField("ThrowHook")!.SetValue(pair.Replica, true);
        int other = 0;
        Slots(pair.Replica).Changed += (in SyncDictionaryChange<int, int> c) => other++;
        Slots(pair.Authority)[0] = 1;
        Assert.Equal(1, Slots(pair.Replica)[0]); Assert.Equal(1, other); Assert.Single(pair.Errors);
        Slots(pair.Authority).Changed += (in SyncDictionaryChange<int, int> c) => Slots(pair.Authority)[99] = 99;
        Slots(pair.Authority)[0] = 2;
        Assert.False(Slots(pair.Authority).ContainsKey(99));
        Assert.Contains(pair.Errors, e => e.Message.Contains("reentrantly"));
        pair.Host.Unbind(Key); Assert.Throws<RpcException>(() => Slots(pair.Authority).Clear());
    }

    [Fact]
    public async Task ClientRpcExecutesAtHostAndStateReturnsWithoutClientWritePrivilege()
    {
        using var pair = new Pair(type: "InheritedSyncState");
        var request = (Task<int>)pair.Replica.GetType().GetMethod("SetSlot")!.Invoke(pair.Replica, new object[] { 4, 12 })!;
        Assert.Equal(12, await request);
        Assert.Equal(12, Slots(pair.Replica)[4]); Assert.Equal(12, Property<int>(pair.Replica, "Health"));
        Assert.Equal("client", Field<string>(pair.Authority, "Sender"));
        Assert.Equal(1, Field<int>(pair.Authority, "Calls")); Assert.Equal(0, Field<int>(pair.Replica, "Calls"));
        Assert.Empty(pair.Errors);
    }

    [Fact]
    public void MutableDtoIsOwnedAcrossWritesReadsEnumerationAndCallbacks()
    {
        using var pair = new Pair(type: "DtoSyncState");
        var source = Property<SyncDictionary<int, Fixture.SyncItem>>(pair.Authority, "Items");
        var copy = Property<SyncDictionary<int, Fixture.SyncItem>>(pair.Replica, "Items");
        var list = Property<SyncList<Fixture.SyncItem>>(pair.Authority, "List");
        var item = new Fixture.SyncItem { Count = 3, Name = "owned" };
        source.Changed += (in SyncDictionaryChange<int, Fixture.SyncItem> c) => { if (c.NewValue != null) c.NewValue.Count = 999; };
        source[1] = item; item.Count = 50;
        Assert.Equal(3, source[1].Count); Assert.Equal(3, copy[1].Count);
        var read = source[1]; read.Count = 4; Assert.Equal(3, source[1].Count);
        source[1] = read; Assert.Equal(4, source[1].Count); Assert.Equal(4, copy[1].Count);
        source.Values.First().Count = 80; source.First().Value.Count = 90;
        source.TryGetValue(1, out var fetched); fetched.Count = 100;
        Assert.Equal(4, source[1].Count);
        long version = source.Version; source[1] = new Fixture.SyncItem { Count = 4, Name = "owned" }; Assert.Equal(version, source.Version);
        list.Add(read); read.Count = 500; list.First().Count = 600;
        Assert.Equal(4, list[0].Count); Assert.True(list.Remove(new Fixture.SyncItem { Count = 4, Name = "owned" }));
        Assert.Empty(pair.Errors);
    }

    [Fact]
    public async Task SnapshotRecoveryRetriesAreBoundedAndSurviveOneLostResponse()
    {
        using var pair = new Pair();
        bool dropDelta = true; int droppedSnapshots = 0;
        pair.HW.Drop = p => p.Kind == "stateDelta" && dropDelta ||
            p.Kind == "state" && p.Property == "Slots" && Interlocked.Increment(ref droppedSnapshots) == 1;
        Slots(pair.Authority)[1] = 1; dropDelta = false; Slots(pair.Authority)[2] = 2;
        Assert.Empty(Slots(pair.Replica));
        await Until(() => Slots(pair.Replica).Count == 2);
        Assert.InRange(droppedSnapshots, 2, 3); Assert.Empty(pair.Errors);
        pair.Client.RequestStateSnapshot(Key); Assert.Equal(2, Slots(pair.Replica).Count);
    }

    [Fact]
    public async Task ConcurrentHostWritersMaintainOneOrderedRevisionStream()
    {
        using var pair = new Pair();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(key => Task.Run(() =>
        {
            for (int value = 0; value < 10; value++) Slots(pair.Authority)[key] = value;
        })));
        await Until(() => Slots(pair.Replica).Version == 80);
        Assert.All(Slots(pair.Replica), entry => Assert.Equal(9, entry.Value));
        Assert.Equal(8, Slots(pair.Replica).Count); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void InvalidSecondBatchEditLeavesWholeCollectionUnchanged()
    {
        using var pair = new Pair();
        Tasks(pair.Authority).Add("stable"); long version = Tasks(pair.Authority).Version;
        Assert.Throws<RpcException>(() => Tasks(pair.Authority).AddRange(new[] { "good", new string('x', 20000) }));
        Assert.Equal(new[] { "stable" }, Tasks(pair.Authority).ToArray());
        Assert.Equal(version, Tasks(pair.Authority).Version); Assert.Equal(version, Tasks(pair.Replica).Version);
        pair.CW.Leave(HostId);
        Assert.Throws<RpcException>(() => Tasks(pair.Replica).Add("disconnected"));
    }

    [Fact]
    public void MixedOperationBatchIsOneRevisionOnePacketAndCallbacksSeeCommittedState()
    {
        using var pair = new Pair();
        Slots(pair.Authority)[1] = 10;
        int before = pair.HW.Packets("stateDelta").Length;
        long version = Slots(pair.Authority).Version;
        bool sawComplete = false;
        Slots(pair.Replica).Changed += (in SyncDictionaryChange<int, int> change) =>
        { if (change.Version == version + 1) sawComplete = !Slots(pair.Replica).ContainsKey(1) && Slots(pair.Replica)[2] == 10; };
        Slots(pair.Authority).ApplyBatch(SyncDictionary<int, int>.Edit.Remove(1), SyncDictionary<int, int>.Edit.Set(2, 10));
        Assert.True(sawComplete); Assert.Equal(version + 1, Slots(pair.Replica).Version);
        Assert.Equal(before + 1, pair.HW.Packets("stateDelta").Length);
        Tasks(pair.Authority).ApplyBatch(SyncList<string>.Edit.Add("a"), SyncList<string>.Edit.Insert(0, "b"), SyncList<string>.Edit.RemoveAt(1));
        Assert.Equal(new[] { "b" }, Tasks(pair.Replica).ToArray()); Assert.Equal(1, Tasks(pair.Replica).Version);
        Unlocks(pair.Authority).ApplyBatch(SyncHashSet<int>.Edit.Add(1), SyncHashSet<int>.Edit.Remove(1), SyncHashSet<int>.Edit.Add(2));
        Assert.Equal(new[] { 2 }, Unlocks(pair.Replica).ToArray()); Assert.Equal(1, Unlocks(pair.Replica).Version);
        Assert.Throws<RpcException>(() => Tasks(pair.Authority).ApplyBatch(SyncList<string>.Edit.Add("valid"), SyncList<string>.Edit.RemoveAt(999)));
        Assert.Equal(new[] { "b" }, Tasks(pair.Authority).ToArray()); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void SingleEditDeltaSizeDoesNotGrowWithCollectionContents()
    {
        using var pair = new Pair();
        Slots(pair.Authority)[0] = 1;
        int sparse = pair.HW.Packets("stateDelta").Last().Value!.Length;
        for (int batch = 0; batch < 4; batch++)
            Slots(pair.Authority).SetRange(Enumerable.Range(batch * 64, 64).Select(i => new KeyValuePair<int, int>(i, i)));
        Slots(pair.Authority)[0] = 1000;
        Assert.Equal(sparse, pair.HW.Packets("stateDelta").Last().Value!.Length);
        Assert.Equal(256, Slots(pair.Replica).Count);
        pair.Client.RequestStateSnapshot(Key);
        Assert.True(pair.HW.Packets("state").Last(p => p.Property == "Slots").Value!.Length > sparse * 50);
        Assert.Empty(pair.Errors);
    }

    [Fact]
    public void NoOpIntAssignmentAllocatesNothingAfterWarmup()
    {
        using var pair = new Pair(type: "SyncPerfState");
        var map = Property<SyncDictionary<int, int>>(pair.Authority, "Map"); map[0] = 7;
        for (int i = 0; i < 128; i++) map[0] = 7;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) map[0] = 7;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(1, map.Version); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void SingleDeltaWithTrailingGarbageCannotCommitAndReusedStagesRecover()
    {
        using var pair = new Pair(); Slots(pair.Authority)[0] = 1;
        var packet = pair.HW.Packets("stateDelta").Last(); packet.Version = 2;
        using var body = new BinaryBufferWriter(128);
        SyncWire.Int(body, 1); SyncWire.Byte(body, (byte)SyncOperation.Set);
        SyncValue<int>.Write(body, 0); SyncValue<int>.Write(body, 2); SyncWire.Byte(body, 255);
        packet.Value = SyncWire.Wrap(2, Fingerprint(pair.Replica, "Slots"), 1, body.CopyOwned());
        pair.CW.Inject(HostId, ReliableCodec.Encode(packet));
        Assert.Equal(1, Slots(pair.Replica)[0]); Assert.Equal(1, Slots(pair.Replica).Version);
        Assert.Single(pair.Errors); pair.Errors.Clear();
        for (int i = 0; i < 10; i++)
            Assert.Throws<RpcException>(() => Tasks(pair.Authority).ApplyBatch(SyncList<string>.Edit.Add("discard"), SyncList<string>.Edit.RemoveAt(999)));
        Tasks(pair.Authority).ApplyBatch(SyncList<string>.Edit.Add("keep"), SyncList<string>.Edit.Add("also keep"));
        Slots(pair.Authority)[0] = 2;
        Assert.Equal(new[] { "keep", "also keep" }, Tasks(pair.Replica).ToArray());
        Assert.Equal(2, Slots(pair.Replica)[0]); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void ScratchWriterPoolSeparatesNestedLeasesAndOwnedResults()
    {
        byte[] owned;
        using (var first = BinaryBufferWriter.Rent(256))
        {
            first.Write(new byte[] { 1, 2, 3 });
            using (var nested = BinaryBufferWriter.Rent(64))
            { nested.Write(new byte[] { 9, 8, 7 }); Assert.True(first.WrittenSpan.SequenceEqual(new byte[] { 1, 2, 3 })); }
            owned = first.CopyOwned();
        }
        using (var reuse = BinaryBufferWriter.Rent(32))
        {
            reuse.Write(new byte[] { 4, 5, 6 });
            Assert.Throws<RpcException>(() => reuse.GetSpan(33));
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, owned);
    }

    [Fact]
    public async Task RestoringBindingCannotAcceptWritesBeforeInitializationFinishes()
    {
        using var pair = new Pair();
        Slots(pair.Authority)[1] = 17; pair.Host.Unbind(Key);
        var replacement = New(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Slots(replacement).Changed += (in SyncDictionaryChange<int, int> change) =>
        { if (change.Operation == SyncOperation.Reset) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } };
        var bind = Task.Run(() => pair.Host.Bind(Key, replacement, _ => true));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(RpcError.Disposed, Assert.Throws<RpcException>(() => Slots(replacement)[9] = 9).Error);
        }
        finally { release.Set(); }
        await bind.WaitAsync(TimeSpan.FromSeconds(5));
        Slots(replacement)[9] = 9;
        Assert.Equal(17, Slots(replacement)[1]); Assert.Equal(9, Slots(pair.Replica)[9]); Assert.Empty(pair.Errors);
    }

    [Fact]
    public void MissingMemberInOtherwiseIdenticalContractFailsBeforeApplyingCollection()
    {
        using var pair = new Pair(false);
        Slots(pair.Authority)[1] = 123;
        var path = Path.Combine(Path.GetTempPath(), "sync-schema-" + Guid.NewGuid().ToString("N") + ".dll");
        using (var module = Mono.Cecil.ModuleDefinition.ReadModule(RuntimeTests.WovenAssembly.Location, new Mono.Cecil.ReaderParameters { InMemory = true }))
        {
            module.Assembly.Name.Name = "SyncSchema_" + Guid.NewGuid().ToString("N");
            var health = module.Types.Single(t => t.Name == "SyncState").Properties.Single(p => p.Name == "Health");
            health.CustomAttributes.Remove(health.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(SyncVarAttribute).FullName));
            module.Write(path);
        }
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var incompatible = Activator.CreateInstance(assembly.GetType("Fixture.SyncState")!)!;
        var exception = Assert.Throws<RpcException>(() => pair.Client.Bind(Key, incompatible));
        Assert.Contains("fingerprint", exception.Message);
        Assert.Empty(Slots(incompatible));
        Assert.Throws<RpcException>(() => Slots(incompatible)[1] = 2);
    }

    [Fact]
    public void WeaverRejectsWrongHooksReplaceableCollectionsAndUnstableKeys()
    {
        string input = RuntimeTests.FixturePath("InvalidFixtures");
        var errors = BITKit.Multiplayer.CodeGen.Weaver.Weave(input, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll"));
        Assert.Contains(errors, x => x.Contains("ReplaceableCollection") && x.Contains("getter-only"));
        Assert.Contains(errors, x => x.Contains("WrongSyncHook") && x.Contains("Hook must"));
        Assert.Contains(errors, x => x.Contains("AsyncSyncHook") && x.Contains("Hook must"));
        Assert.Contains(errors, x => x.Contains("OrphanSyncHook") && x.Contains("Hook requires"));
        Assert.Contains(errors, x => x.Contains("BadSetKey") && x.Contains("primitive/enum/string"));
    }

}
