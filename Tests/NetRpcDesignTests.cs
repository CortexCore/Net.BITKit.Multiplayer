using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.RemoteCompiler;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using Xunit;
using NetTransport = BITKit.Multiplayer.NetRpc.ITransport;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcDesignTests
{
    public sealed class Box { public int Number { get; set; } }
    public interface IGame
    {
        Task<int> Plus(int a, int b);
        UniTask<int> UniPlus(int a, int b);
        UniTask UniWait();
        Task<string?> Null();
        Task Delay(int milliseconds);
        Task<int> DelayValue(int value, int milliseconds);
        Task Fail();
        ValueTask<int> Value(int value);
        void Fire(int damage);
        Task Damage(int damage);
        int Health { get; }
        IList<int> Items { get; }
        IDictionary<int, int> Counts { get; }
        Box Status { get; }
        IList<Box> Packets { get; }
    }
    public sealed class Game : IGame
    {
        public NetComponent<int> HealthComponent { get; } = new(1, 100);
        public int Health => HealthComponent.Value;
        public IList<int> Items { get; } = new NetworkList<int>();
        public IDictionary<int, int> Counts { get; } = new NetworkDictionary<int, int>();
        public Box Status { get; } = new() { Number = 100 };
        public IList<Box> Packets { get; } = new NetworkList<Box>();
        public int LastDamage;
        public Task<int> Plus(int a, int b) => Task.FromResult(a + b);
        public UniTask<int> UniPlus(int a, int b) => UniTask.FromResult(a + b);
        public UniTask UniWait() => UniTask.CompletedTask;
        public Task<string?> Null() => Task.FromResult<string?>(null);
        public Task Delay(int milliseconds) => Task.Delay(milliseconds);
        public async Task<int> DelayValue(int value, int milliseconds) { await Task.Delay(milliseconds); return value; }
        public Task Fail() => Task.FromException(new InvalidOperationException("expected remote fault"));
        public ValueTask<int> Value(int value) => ValueTask.FromResult(value);
        public void Fire(int damage) => LastDamage = damage;
        public Task Damage(int damage) { HealthComponent.Value -= damage; Items.Add(damage); Counts[damage] = 1; return Task.CompletedTask; }
    }
    internal sealed class Pair : NetTransport
    {
        public Pair Other = null!;
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public Func<ReadOnlyMemory<byte>, bool>? Drop;
        public System.Collections.Concurrent.ConcurrentQueue<byte[]> Sent = new();
        public int FastCount;
        public static (Pair A, Pair B) Create() { var a = new Pair(); var b = new Pair(); a.Other = b; b.Other = a; return (a, b); }
        public UniTask Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        { var owned = data.ToArray(); Sent.Enqueue(owned); if (Drop?.Invoke(owned) != true) Other.OnReceived?.Invoke(owned); return UniTask.CompletedTask; }
        public UniTask SendFast(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { FastCount++; return Send(data, cancellationToken); }
        public void Deliver(byte[] packet) => OnReceived?.Invoke(packet);
    }
    private sealed class BorrowedPair : NetTransport
    {
        public BorrowedPair Other = null!;
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length); payload.Span.CopyTo(buffer);
            try { Other.OnReceived?.Invoke(buffer.AsMemory(0, payload.Length)); }
            finally { buffer.AsSpan().Fill(0xCC); System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            return UniTask.CompletedTask;
        }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Send(payload, cancellationToken);
    }
    internal static ServiceProvider Host(NetTransport transport, ulong scope = 0) => new ServiceCollection()
        .AddGeneratedRemoteInterfaces().AddNetRpcService<IGame, Game>().AddNetRpc(true, _ => transport, scope).BuildServiceProvider();
    internal static ServiceProvider Client(NetTransport transport, ulong scope = 0) => new ServiceCollection()
        .AddGeneratedRemoteInterfaces().AddRemoteInterface<IGame>().AddNetRpc(false, _ => transport, scope).BuildServiceProvider();
    private static NetEntity Entity(INetComponent component) => new(new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(7)).AddSingleton(component).BuildServiceProvider());

    [Fact]
    public async Task GeneratedDiProxyUsesTypedReceiverAndAwaitedCompletionIncludingNullAndFault()
    {
        var (a, b) = Pair.Create(); using var host = Host(b); using var client = Client(a);
        _ = host.GetRequiredService<RpcContextService>(); var proxy = client.GetRequiredService<IGame>();
        Assert.StartsWith("NetRemote_", proxy.GetType().Name);
        Assert.Equal(42, await proxy.Plus(20, 22)); Assert.Equal(17, await proxy.Value(17));
        Assert.Equal(42, await proxy.UniPlus(20, 22)); await proxy.UniWait(); Assert.Null(await proxy.Null());
        var delay = proxy.Delay(40); Assert.False(delay.IsCompleted); await delay;
        var error = await Assert.ThrowsAsync<RpcException>(() => proxy.Fail()); Assert.Contains("expected remote fault", error.Message);
        proxy.Fire(12); Assert.Equal(12, host.GetRequiredService<Game>().LastDamage);
        Assert.All(a.Sent.Select(p => NetRpcCodec.Decode(p)).Where(m => m.Kind == NetRpcMessageKind.Call && m.MethodId == RpcContextService.RpcMethodId(typeof(IGame), typeof(IGame).GetMethod("Fire")!)), m => Assert.Equal(0u, m.RequestId));
    }
    [Fact]
    public async Task InterfaceScalarCollectionsAndEcsApplyHostAuthorityThenRejectClientWrites()
    {
        var (a, b) = Pair.Create(); using var host = Host(b); using var client = Client(a);
        var hr = host.GetRequiredService<RpcContextService>(); var cr = client.GetRequiredService<RpcContextService>();
        var hostGame = host.GetRequiredService<Game>(); var localHealth = new NetComponent<int>(1);
        host.GetRequiredService<IEntitiesService>().Register(Entity(hostGame.HealthComponent)); client.GetRequiredService<IEntitiesService>().Register(Entity(localHealth));
        var proxy = client.GetRequiredService<IGame>(); Assert.Equal(100, proxy.Health);
        await proxy.Damage(10); await hr.PublishStateAsync();
        Assert.Equal(90, localHealth.Value); Assert.Equal(90, proxy.Health); Assert.Equal(new[] { 10 }, proxy.Items); Assert.Equal(1, proxy.Counts[10]);
        Assert.True(b.FastCount > 0);
        Assert.Throws<RpcException>(() => localHealth.Value = 0); Assert.Throws<RpcException>(() => proxy.Items.Add(99)); Assert.Throws<RpcException>(() => proxy.Counts.Clear());
        var count = b.FastCount; await hr.PublishStateAsync(); Assert.Equal(count, b.FastCount);
        hostGame.Items[0] = 11; hostGame.Items.Insert(0, 2); hostGame.Items.RemoveAt(1); Assert.Equal(new[] { 2 }, proxy.Items);
        hostGame.Counts.Add(3, 4); hostGame.Counts[3] = 5; hostGame.Counts.Remove(10); Assert.Equal(5, proxy.Counts[3]);
        hostGame.Items.Clear(); hostGame.Counts.Clear(); Assert.Empty(proxy.Items); Assert.Empty(proxy.Counts);
    }
    [Fact]
    public async Task DroppedCollectionDeltaRequestsSnapshotAndDuplicateDoesNotRunChangedTwice()
    {
        var (a, b) = Pair.Create(); using var host = Host(b); using var client = Client(a);
        _ = host.GetRequiredService<RpcContextService>(); var proxy = client.GetRequiredService<IGame>(); var game = host.GetRequiredService<Game>();
        bool dropped = false;
        b.Drop = bytes => { var m = NetRpcCodec.Decode(bytes); if (m.Kind != NetRpcMessageKind.SyncOperation || dropped) return false; dropped = true; return true; };
        game.Items.Add(1); game.Items.Add(2); Assert.Equal(new[] { 1, 2 }, proxy.Items);
        int changes = 0; ((NetworkList<int>)proxy.Items).Changed += _ => changes++;
        game.Items.Add(3); Assert.Equal(1, changes);
        var last = b.Sent.Last(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.SyncOperation); a.Deliver(last); Assert.Equal(1, changes);
        dropped = false; game.Counts[1] = 10; game.Counts[2] = 20; Assert.Equal(10, proxy.Counts[1]); Assert.Equal(20, proxy.Counts[2]);
    }
    [Fact]
    public async Task TwoScopesRejectCrossFramesAndComponentOldSnapshotAndUnregistration()
    {
        var (a, b) = Pair.Create(); using var host = Host(b, 123); using var client = Client(a, 123);
        var hr = host.GetRequiredService<RpcContextService>(); _ = client.GetRequiredService<RpcContextService>();
        var hc = new NetComponent<int>(1, 100); var cc = new NetComponent<int>(1); var entity = Entity(cc);
        host.GetRequiredService<IEntitiesService>().Register(Entity(hc)); client.GetRequiredService<IEntitiesService>().Register(entity);
        await hr.PublishStateAsync(); var old = b.Sent.Last(); hc.Value = 50; await hr.PublishStateAsync(); a.Deliver(old); Assert.Equal(50, cc.Value);
        var frame = NetRpcCodec.Decode(old); a.Deliver(NetRpcCodec.Encode(new NetRpcModel(frame.Kind, frame.TargetId, frame.MethodId, 0, frame.ArgumentCount, frame.Payload, 999))); Assert.Equal(50, cc.Value);
        client.GetRequiredService<IEntitiesService>().Unregister(entity); hc.Value = 25; await hr.PublishStateAsync(); Assert.Equal(50, cc.Value);
    }
    [Fact]
    public async Task RealTcpAndUdpCarryGeneratedProxyAndComponentSnapshot()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var accepted = listener.AcceptAsync();
        await using var ct = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port); await using var ht = await accepted;
        using var host = Host(ht); using var client = Client(ct); var hr = host.GetRequiredService<RpcContextService>(); _ = client.GetRequiredService<RpcContextService>();
        var hc = new NetComponent<int>(1, 100); var cc = new NetComponent<int>(1);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); cc.Changed += (_, next) => received.TrySetResult(next);
        host.GetRequiredService<IEntitiesService>().Register(Entity(hc)); client.GetRequiredService<IEntitiesService>().Register(Entity(cc));
        Assert.Equal(42, await client.GetRequiredService<IGame>().Plus(20, 22));
        await hr.PublishStateAsync(); Assert.Equal(100, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TimedOutPreAdmissionHandshakeDoesNotStopDirectListener()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        var first = listener.AcceptAsync(TimeSpan.FromMilliseconds(150));
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Loopback, listener.EndPoint.Port);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.AsTask());

        var next = listener.AcceptAsync(TimeSpan.FromSeconds(3));
        await using var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port);
        await using var host = await next;
        Assert.NotNull(host);
    }

    [Fact]
    public void WarmTypedMessageBagHasNoPerValueAllocations()
    {
        for (int i = 0; i < 100; i++) { using var bag = NetMessageBag.Pool(); bag.Write(42); bag.Write(1.25f); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { using var bag = NetMessageBag.Pool(); bag.Write(42); bag.Write(1.25f); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task PendingRequestTimesOutCancelsAndFailsOnScopeDisposal()
    {
        var (a, _) = Pair.Create(); using var provider = new ServiceCollection().BuildServiceProvider();
        using var runtime = new RpcContextService(provider, a, false) { RequestTimeout = TimeSpan.FromMilliseconds(30) };
        var context = runtime.CreateContext(7); var frame = new NetRpcModel(NetRpcMessageKind.Call, 7, 8, 0, 0, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(RpcError.Timeout, (await Assert.ThrowsAsync<RpcException>(() => context.RequestTask<int>(frame))).Error);
        using var cancelled = new CancellationTokenSource(); var waiting = context.RequestTask<int>(frame, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        runtime.RequestTimeout = TimeSpan.FromSeconds(5); var disposed = context.RequestTask<int>(frame); runtime.Dispose();
        Assert.Equal(RpcError.Disposed, (await Assert.ThrowsAsync<RpcException>(() => disposed)).Error);
    }

    [Fact]
    public async Task ReplacementConnectionCannotReceiveOldPeersPendingReply()
    {
        var (a, b) = Pair.Create(); using var host = Host(b); var runtime = host.GetRequiredService<RpcContextService>();
        using var oldClient = Client(a); var oldProxy = oldClient.GetRequiredService<IGame>();
        var oldResult = oldProxy.DelayValue(111, 100);
        oldClient.GetRequiredService<RpcContextService>().DetachPeer(1); runtime.DetachPeer(2);
        var (nextA, nextB) = Pair.Create(); runtime.AttachPeer(2, nextB); using var nextClient = Client(nextA);
        Assert.Equal(222, await nextClient.GetRequiredService<IGame>().DelayValue(222, 200));
        await Assert.ThrowsAsync<RpcException>(() => oldResult);
    }

    [Fact]
    public async Task IndependentRoomsWithIdenticalIdsAndExplicitAuthorizationDoNotLeak()
    {
        var (a, b) = Pair.Create(); var (c, d) = Pair.Create();
        using var h1 = Host(b, 11); using var h2 = Host(d, 22); using var c1 = Client(a, 11); using var c2 = Client(c, 22);
        var r1 = h1.GetRequiredService<RpcContextService>(); var r2 = h2.GetRequiredService<RpcContextService>();
        var p1 = c1.GetRequiredService<IGame>(); var p2 = c2.GetRequiredService<IGame>();
        await p1.Damage(10); await p2.Damage(20); await r1.PublishStateAsync(); await r2.PublishStateAsync();
        Assert.Equal(90, p1.Health); Assert.Equal(80, p2.Health); Assert.Equal(new[] { 10 }, p1.Items); Assert.Equal(new[] { 20 }, p2.Items);
        var foreign = b.Sent.Last(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.SyncSnapshot); c.Deliver(foreign); Assert.Equal(80, p2.Health);
        r1.Authorize = (_, _, _) => false;
        Assert.Equal(RpcError.Unauthorized, (await Assert.ThrowsAsync<RpcException>(() => p1.Damage(90))).Error);
        Assert.Equal(90, h1.GetRequiredService<Game>().Health);
    }

    [Fact]
    public async Task GeneratedProxyCanSupplyDynamicMapWithoutClientImplementation()
    {
        var (a, b) = Pair.Create(); using var hp = new ServiceCollection().AddSingleton<IGame, Game>().BuildServiceProvider();
        using var host = new RpcContextService(hp, b, true);
        using var client = Client(a); Assert.Equal(42, await client.GetRequiredService<IGame>().Plus(20, 22));
        Assert.Contains(b.Sent, p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.MapRequest);
        Assert.Contains(a.Sent, p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.MapResponse);
    }

    [Fact]
    public async Task InvalidSchemaFingerprintAndUnknownEntityNeverChangeLocalState()
    {
        var (a, b) = Pair.Create(); using var h = Host(b); using var c = Client(a);
        var host = h.GetRequiredService<RpcContextService>(); var client = c.GetRequiredService<RpcContextService>();
        var hc = new NetComponent<int>(1, 100); var cc = new NetComponent<int>(1);
        h.GetRequiredService<IEntitiesService>().Register(Entity(hc)); c.GetRequiredService<IEntitiesService>().Register(Entity(cc));
        await host.PublishStateAsync(); var good = b.Sent.Last(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.Component); var model = NetRpcCodec.Decode(good);
        Exception? fault = null; client.Faulted += e => fault = e;
        using var bad = NetMessageBag.Pool(); bad.Write(999UL); bad.Write(99L); bad.Write(0UL); bad.Write(5);
        a.Deliver(NetRpcCodec.Encode(new NetRpcModel(model.Kind, model.TargetId, model.MethodId, 0, bad.Count, bad.Memory)));
        Assert.IsType<RpcException>(fault); Assert.Equal(100, cc.Value);
        a.Deliver(NetRpcCodec.Encode(new NetRpcModel(model.Kind, 1234, model.MethodId, 0, model.ArgumentCount, model.Payload))); Assert.Equal(100, cc.Value);
        hc.Value = 90; await host.PublishStateAsync(); Assert.Equal(90, cc.Value);
    }

    [Fact]
    public async Task AutomaticUnreliableSnapshotsHealLostLastUpdateAndIgnoreEntitiesWithoutIdentity()
    {
        var (a, b) = Pair.Create();
        using var hp = new ServiceCollection().AddNetRpc(true, _ => b, options: new NetRpcOptions { SyncInterval = TimeSpan.FromMilliseconds(20), SnapshotInterval = TimeSpan.FromMilliseconds(100) }).BuildServiceProvider();
        using var cp = new ServiceCollection().AddNetRpc(false, _ => a).BuildServiceProvider();
        _ = hp.GetRequiredService<RpcContextService>(); _ = cp.GetRequiredService<RpcContextService>();
        var hc = new NetComponent<int>(1, 123); var cc = new NetComponent<int>(1);
        var withoutIdentity = new NetComponent<int>(2, 999);
        hp.GetRequiredService<IEntitiesService>().Register(new NetEntity(new ServiceCollection().AddSingleton<INetComponent>(withoutIdentity).BuildServiceProvider()));
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); cc.Changed += (_, next) => received.TrySetResult(next);
        bool dropped = false; b.Drop = bytes => { if (NetRpcCodec.Decode(bytes).Kind != NetRpcMessageKind.Component || dropped) return false; dropped = true; return true; };
        hp.GetRequiredService<IEntitiesService>().Register(Entity(hc)); cp.GetRequiredService<IEntitiesService>().Register(Entity(cc));
        Assert.Equal(123, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(b.Sent, p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.Component && NetRpcCodec.Decode(p).MethodId == 2);
    }

    [Fact]
    public async Task LateJoinGetsCurrentScalarAndCollectionSnapshotsAndMalformedDeltaIsAtomic()
    {
        var (a, b) = Pair.Create(); using var hp = Host(b); var host = hp.GetRequiredService<RpcContextService>(); var game = hp.GetRequiredService<Game>();
        game.Items.Add(1); game.Counts[2] = 3; game.HealthComponent.Value = 70;
        using var cp = Client(a); var client = cp.GetRequiredService<RpcContextService>(); var proxy = cp.GetRequiredService<IGame>();
        Assert.Equal(70, proxy.Health); Assert.Equal(new[] { 1 }, proxy.Items); Assert.Equal(3, proxy.Counts[2]);
        var operation = b.Sent.First(p => NetRpcCodec.Decode(p).Kind == NetRpcMessageKind.SyncOperation); var model = NetRpcCodec.Decode(operation);
        using var reader = new NetMessageReader(model.Payload, model.ArgumentCount); var schema = reader.Read<ulong>();
        using var bad = NetMessageBag.Pool(); bad.Write(schema); bad.Write(((NetworkList<int>)proxy.Items).Revision + 1); bad.Write((byte)NetworkOperation.Set); bad.Write(999); bad.Write(7);
        Exception? fault = null; client.Faulted += e => fault = e; int changes = 0; ((NetworkList<int>)proxy.Items).Changed += _ => changes++;
        a.Deliver(NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.SyncOperation, model.TargetId, model.MethodId, 0, bad.Count, bad.Memory)));
        Assert.NotNull(fault); Assert.Equal(new[] { 1 }, proxy.Items); Assert.Equal(0, changes);
        game.Items.Add(4); Assert.Equal(new[] { 1, 4 }, proxy.Items); Assert.Equal(1, changes);
    }

    [Fact]
    public void CollectionCapacityAndDictionaryKeysAreBoundedAndInvalidEditsDoNotCommit()
    {
        var list = new NetworkList<int>(); for (int i = 0; i < NetworkCollection.MaximumCount; i++) list.Add(i);
        var revision = list.Revision;
        Assert.Equal(RpcError.LimitExceeded, Assert.Throws<RpcException>(() => list.Add(5)).Error); Assert.Equal(revision, list.Revision);
        var dictionary = new NetworkDictionary<string, int>();
        Assert.Throws<RpcException>(() => dictionary.Add(null!, 1)); Assert.Empty(dictionary); Assert.Equal(0, dictionary.Revision);
        dictionary.Add("one", 1); Assert.Throws<ArgumentException>(() => dictionary.Add("one", 2)); Assert.Equal(1, dictionary["one"]); Assert.Equal(1, dictionary.Revision);
    }

    [Fact]
    public async Task MutableDtoReadsCannotModifyAuthoritativeClientCachesOrCollectionElements()
    {
        var (a, b) = Pair.Create(); using var hp = Host(b); using var cp = Client(a);
        var host = hp.GetRequiredService<RpcContextService>(); _ = cp.GetRequiredService<RpcContextService>();
        var game = hp.GetRequiredService<Game>(); var proxy = cp.GetRequiredService<IGame>();
        proxy.Status.Number = 0; Assert.Equal(100, proxy.Status.Number); Assert.Equal(100, game.Status.Number);
        var box = new Box { Number = 42 }; game.Packets.Add(box); box.Number = 7;
        Assert.Equal(42, game.Packets[0].Number); proxy.Packets[0].Number = 0; Assert.Equal(42, proxy.Packets[0].Number);
        var hc = new NetComponent<Box>(1, new Box { Number = 10 }); var cc = new NetComponent<Box>(1, new Box());
        hp.GetRequiredService<IEntitiesService>().Register(Entity(hc)); cp.GetRequiredService<IEntitiesService>().Register(Entity(cc));
        await host.PublishStateAsync(); cc.Value.Number = 0; Assert.Equal(10, cc.Value.Number);
        hc.Value.Number = 20; await host.PublishStateAsync(); Assert.Equal(20, cc.Value.Number);
    }
    [Fact]
    public async Task BorrowedReceiveBuffersCanBePoisonedImmediatelyWithoutCorruptingAsyncCallsOrReplies()
    {
        var a = new BorrowedPair(); var b = new BorrowedPair(); a.Other = b; b.Other = a;
        using var host = Host(b); using var client = Client(a); _ = host.GetRequiredService<RpcContextService>();
        var proxy = client.GetRequiredService<IGame>();
        Assert.Equal(42, await proxy.DelayValue(42, 30)); await proxy.Damage(10);
        Assert.Equal(new[] { 10 }, proxy.Items); Assert.Equal(1, proxy.Counts[10]);
        Assert.Contains("expected remote fault", (await Assert.ThrowsAsync<RpcException>(() => proxy.Fail())).Message);
    }
    [Fact]
    public async Task TcpFrameLimitIncludesTheNativeHeaderAndRelayEnvelopeBudget()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var accepting = listener.AcceptAsync();
        await using var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port); await using var server = await accepting;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnReceived += bytes => received.TrySetResult(bytes.ToArray());
        var frame = new byte[NetRpcCodec.HeaderBytes + NetRpcCodec.MaxPayloadBytes + 6]; frame[0] = 42; frame[^1] = 99;
        await client.Send(frame); var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(frame.Length, actual.Length); Assert.Equal(42, actual[0]); Assert.Equal(99, actual[^1]);
    }
    [Fact]
    public async Task AuthenticatedUdpProofLearnsRemappedPortAndPreservesOpaquePayloads()
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var accepting = listener.AcceptAsync();
        await using var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port); await using var server = await accepting;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!client.IsUnreliableReady || !server.IsUnreliableReady) await Task.Delay(10, deadline.Token);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var token = (byte[])typeof(TcpTransport).GetField("_sendToken", flags)!.GetValue(client)!;
        var serverUdp = (System.Net.Sockets.UdpClient)typeof(TcpTransport).GetField("_udp", flags)!.GetValue(server)!;
        using var remapped = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var proof = new byte[17]; token.CopyTo(proof, 0); proof[16] = 1;
        await remapped.SendAsync(proof, proof.Length, (IPEndPoint)serverUdp.Client.LocalEndPoint!);
        var acknowledgment = await remapped.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, acknowledgment.Buffer[16]);
        await server.SendFast(new byte[] { 0 });
        var data = await remapped.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(18, data.Buffer.Length); Assert.Equal(0, data.Buffer[16]); Assert.Equal(0, data.Buffer[17]);
    }
    [Fact]
    public void LogicalPeerDisconnectIsOncePerBindingAndAllowsScopedReplacement()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); using var runtime = new RpcContextService(services, true, 7);
        var (a, _) = Pair.Create(); runtime.AttachPeer(2, a); int notifications = 0;
        runtime.PeerDisconnected += peer => { Assert.Equal(2u, peer); notifications++; };
        runtime.DetachPeer(2); runtime.DetachPeer(2); Assert.Equal(1, notifications);
        var (next, _) = Pair.Create(); runtime.AttachPeer(2, next); runtime.DetachPeer(2); Assert.Equal(2, notifications);
        runtime.Dispose(); Assert.Equal(2, notifications);
    }
}
