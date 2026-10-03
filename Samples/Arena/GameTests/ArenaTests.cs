using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;
using BITKit.Multiplayer.Samples.Arena.App;
using Xunit;

namespace BITKit.Multiplayer.Samples.Arena;

public sealed class ArenaTests
{
    private sealed class CountingNativeFactory : ITransportFactory
    {
        private int created;
        public string Name => "counting-native-udp";
        public int Created => Volatile.Read(ref created);
        public ITransport Create()
        {
            Interlocked.Increment(ref created);
            return new UdpTransport();
        }
    }

    [Fact] public async Task DiOverrideCreatesNativeEndpointsForDirectHostClientAndRebind()
    {
        var factory = new CountingNativeFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ITransportFactory>(factory);
        services.AddUdpTransport(); // TryAdd must not replace an explicit application override.
        using var provider = services.BuildServiceProvider();
        var selected = provider.GetRequiredService<ITransportFactory>();
        Assert.Same(factory, selected);
        var lobby = new FakeLobby();
        var hostAuth = await lobby.GuestAsync("NativeHost");
        var clientAuth = await lobby.GuestAsync("NativeClient");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth, new ArenaHostOptions { Port = port }, transportFactory: selected);
        await using var client = await ArenaSession.StartClientAsync(lobby, clientAuth,
            new ArenaClientOptions { RoomId = host.RoomId }, transportFactory: selected);
        Assert.Equal(factory.Name, host.TransportName);
        Assert.Equal(factory.Name, client.TransportName);
        await Until(() => host.GetDatagramReport().SentDatagrams > 0 && client.GetDatagramReport().ReceivedDatagrams > 0);
        Assert.True(factory.Created >= 2, "Both direct room wires must create native UDP endpoints through DI.");
        int before = factory.Created;
        await client.RebindUdpAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => client.GetDatagramReport().Ready && client.GetDatagramReport().ReceivedDatagrams > 0);
        Assert.True(factory.Created > before, "Rebind must create a fresh endpoint through the same injected factory.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicDiagnosticReportWaitsForTemporaryWindowsReadLockThenLeavesValidJson(bool asynchronous)
    {
        if (!OperatingSystem.IsWindows()) return; // POSIX permits renaming an open target.
        var directory = Path.Combine(Path.GetTempPath(), "arena-report-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            File.WriteAllText(path, "{\"sequence\":1}");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task writer;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                writer = Task.Run(async () =>
                {
                    started.SetResult();
                    if (asynchronous) await ArenaAtomicFile.WriteAsync(path, "{\"sequence\":2}", CancellationToken.None);
                    else ArenaAtomicFile.Write(path, "{\"sequence\":2}");
                });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(65);
                Assert.False(writer.IsCompleted, "The test must exercise replacement while the old report is locked.");
                Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("sequence").GetInt32());
            }
            await writer.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("sequence").GetInt32());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void AtomicDiagnosticReportFailsBoundedlyWhenReplacementIsPermanentlyDenied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "arena-report-denied-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            File.WriteAllText(path, "{\"sequence\":1}");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var clock = Stopwatch.StartNew();
                var error = Record.Exception(() => ArenaAtomicFile.Write(path, "{\"sequence\":2}"));
                Assert.True(error is IOException or UnauthorizedAccessException, "Persistent permission denial must surface.");
                Assert.InRange(clock.ElapsedMilliseconds, 50, 1500);
                Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("sequence").GetInt32());
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public void WholeProcessGcSamplingWarmsUpAndRetainsOnlyThreeSnapshots()
    {
        var sampler = new ArenaGcSampler(); var report = new ArenaGcReport();
        sampler.Sample(report, 0, ready: true);
        sampler.Sample(report, 2900, ready: true);
        Assert.Null(report.Initial);
        sampler.Sample(report, 3000, ready: true);
        Assert.NotNull(report.Initial);
        sampler.Sample(report, 3300, ready: true);
        Assert.Equal(0, report.SampleCount);
        // This records genuine process-wide counters without forcing a collection.
        sampler.Sample(report, 4000, ready: true);
        Assert.Equal(1, report.SampleCount);
        Assert.Equal(1000, report.WindowMilliseconds);
        Assert.True(report.TotalAllocatedBytes >= 0 && double.IsFinite(report.AllocatedBytesPerSecond));
        Assert.True(report.Final!.ManagedHeapBytes >= 0 && report.PeakManagedHeap!.ManagedHeapBytes >= report.Initial!.ManagedHeapBytes);
        Assert.True(report.Gen0Collections >= 0 && report.Gen1Collections >= 0 && report.Gen2Collections >= 0);
        sampler.Sample(report, 4250, ready: true, final: true);
        Assert.Equal(2, report.SampleCount);
        Assert.Equal(1250, report.WindowMilliseconds);
    }
    [Fact] public void BuiltArenaAppDeploysTheActualUnreliableCodecDependency()
    {
        // An incremental --no-restore build after changing the runtime's package references
        // can leave the executable's assets/deps stale: the woven Host then fails on its
        // first motion tick with FileNotFoundException (MemoryPack.Core), before admission.
        var bin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.FullName;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var app = Path.Combine(bin, "Arena.App", configuration, "net10.0");
        Assert.True(File.Exists(Path.Combine(app, "Arena.App.deps.json")), "Build Arena.App before validating deployment.");
        Assert.True(File.Exists(Path.Combine(app, "MemoryPack.Core.dll")),
            "Arena.App is missing the runtime codec. Restore Arena.App's project graph before building (--no-restore is stale).");
        Assert.True(File.Exists(Path.Combine(app, "BITKit.Multiplayer.Samples.Arena.Contracts.dll")),
            "Arena.App is missing the generated MemoryPack business DTO assembly.");
    }
    [Fact] public void AuthorityRejectsInvalidSequencesAndInputsAndResolvesHitsRespawn()
    {
        var a = new ArenaAuthority();
        Assert.True(a.Add("a", "peer-a", "A")); Assert.True(a.Add("b", "peer-b", "B"));
        Assert.False(a.Move("peer-a", float.NaN, 1, 1));
        Assert.False(a.Move("peer-x", 1, 0, 1));
        Assert.True(a.Move("peer-a", 99, 0, 1)); Assert.False(a.Move("peer-a", -1, 0, 1));
        Assert.Equal(1, a.Fire("peer-a", 1, 0, 1)!.Id);
        Assert.Null(a.Fire("peer-a", 1, 0, 1)); Assert.Null(a.Fire("peer-a", float.PositiveInfinity, 0, 2));
        Assert.Null(a.Fire("peer-a", float.MaxValue, float.MaxValue, 3));
        a.Move("peer-a", 0, 0, 2);
        for (var i = 0; i < 14; i++) a.Step();
        Assert.Equal(75, a.Snapshot("s").Players.Single(p => p.PlayerId == "b").Health);
        for (uint shot = 4; shot <= 6; shot++)
        {
            Assert.NotNull(a.Fire("peer-a", 1, 0, shot));
            for (var i = 0; i < 14; i++) a.Step();
        }
        Assert.Equal(0, a.Snapshot("s").Players.Single(p => p.PlayerId == "b").Health);
        Assert.False(a.Move("peer-b", 0, 1, 1));
        for (var i = 0; i < 61; i++) a.Step();
        Assert.Equal(100, a.Snapshot("s").Players.Single(p => p.PlayerId == "b").Health);
        a.Remove("peer-b"); Assert.Single(a.Snapshot("s").Players);
    }

    [Fact] public async Task HealthCallbacksRunOutsideTheSimulationLock()
    {
        var authority = new ArenaAuthority(); authority.Add("a", "a", "A"); authority.Add("b", "b", "B");
        authority.Fire("a", 1, 0, 1);
        for (var i = 0; i < 12; i++) authority.Step();
        authority.Fire("a", 1, 0, 2);
        await Task.Run(() =>
        {
            for (var i = 0; i < 12; i++)
                authority.Step((_, _) =>
                {
                    // A callback must be able to block on a different worker acquiring Gate.
                    Assert.NotNull(Task.Run(() => authority.Snapshot("s")).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult());
                });
        }).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact] public void MotionSamplingReusesCallerArraysWithoutSnapshotPresentationCopies()
    {
        var authority = new ArenaAuthority(); authority.Add("one", "peer", "One");
        var players = new PlayerMotionUpdate[64]; var bullets = new BulletMotionUpdate[256];
        authority.FillMotion(players, bullets); // warm up dictionary iteration and JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        (int playerCount, int bulletCount, long tick) sample = default;
        for (int i = 0; i < 1000; i++) sample = authority.FillMotion(players, bullets);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(bytes, 0, 128_000); // sampling only; real RPC send allocations reported separately
        Assert.Equal(1, sample.playerCount); Assert.Equal(0, sample.bulletCount);
        Assert.Equal(1, players[0].Id);
    }

    [Fact] public async Task WovenSessionsUseRealTouchSocketTicketAdmissionSyncHealthAndCleanup()
    {
        Assert.NotNull(typeof(ArenaGameService).GetMethod(nameof(ArenaGameService.SubmitMove))!.GetCustomAttributes(typeof(WovenRpcAttribute), false).SingleOrDefault());
        var lobby = new FakeLobby();
        var hostAuth = await lobby.GuestAsync("Host"); var clientAuth = await lobby.GuestAsync("Client");
        var secondAuth = await lobby.GuestAsync("Late");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth, new ArenaHostOptions { Port = port });
        Assert.Equal(ArenaConnectionMode.Direct, host.ConnectionMode);
        Assert.Equal($"127.0.0.1:{port}", host.ConnectedEndpoint);
        using (var intruder = new TouchSocketClientWire(new PeerId(host.LocalPeerId)))
        {
            await intruder.ConnectAsync("127.0.0.1", port, ArenaProtocol.GameToken);
            Assert.Contains("Admission denied", await intruder.RequestAdmissionAsync("invalid-ticket"));
            Assert.Single(host.GetView().Players);
        }
        await using var client = await ArenaSession.StartClientAsync(lobby, clientAuth, new ArenaClientOptions { RoomId = host.RoomId });
        Assert.Equal(ArenaConnectionMode.Direct, client.ConnectionMode);
        Assert.Equal(host.ConnectedEndpoint, client.ConnectedEndpoint);
        await Until(() => host.GetView().Players.Length == 2 && client.GetView().Players.Length == 2);
        // Exercise a real woven Client -> Host reliable request and typed nested DTO return,
        // not merely a local MemoryPack roundtrip or the unreliable motion lane.
        var clientService = (ArenaGameService)typeof(ArenaSessionImpl)
            .GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        RoomSnapshot remoteSnapshot = await clientService.RequestSnapshot().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(host.GetView().Scope, remoteSnapshot.Scope);
        Assert.Equal(2, remoteSnapshot.Players.Length);
        Assert.Contains(remoteSnapshot.Players, p => p.PlayerId == client.LocalPlayerId);
        Assert.Equal(client.LocalPlayerId, client.GetView().Players.Single(p => p.PeerId == client.LocalPeerId).PlayerId);
        host.Fire(1, 0, 1);
        await Until(() => host.GetView().TotalHits > 0 && client.GetView().Players.Single(p => p.PlayerId == client.LocalPlayerId).Health == 75);
        var detached = client.GetView(); detached.Players[0].Health = -999;
        Assert.DoesNotContain(client.GetView().Players, p => p.Health == -999);
        RoomSnapshot departedSnapshot;
        string departedPeer;
        await using (var late = await ArenaSession.StartClientAsync(lobby, secondAuth, new ArenaClientOptions { RoomId = host.RoomId }))
        {
            await Until(() => late.GetView().Players.Length == 3 && late.GetView().TotalHits > 0 && late.GetView().Players.Single(p => p.PlayerId == client.LocalPlayerId).Health == 75);
            Assert.Equal(75, late.GetView().Players.Single(p => p.PlayerId == client.LocalPlayerId).Health);
            departedSnapshot = late.GetView(); departedPeer = late.LocalPeerId;
        }
        await Until(() => host.GetView().Players.Length == 2 && client.GetView().Players.Length == 2);
        departedSnapshot.Tick = client.GetView().Tick; // A delayed equal-tick snapshot must not revive the removed binding.
        typeof(ArenaSessionImpl).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client, new object[] { departedSnapshot });
        Assert.DoesNotContain(client.GetView().Players, p => p.PeerId == departedPeer);
        Assert.Equal("", host.LastError);
        departedSnapshot.Tick += 2; // Even an out-of-order response with a newer tick cannot re-admit a retired peer.
        typeof(ArenaSessionImpl).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client, new object[] { departedSnapshot });
        Assert.DoesNotContain(client.GetView().Players, p => p.PeerId == departedPeer);
    }

    [Fact] public async Task ConcurrentDisconnectAndDamageCompleteWithoutDeadlock()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port });
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var player = await lobby.GuestAsync("Target" + iteration);
            var client = await ArenaSession.StartClientAsync(lobby, player, new ArenaClientOptions { RoomId = host.RoomId });
            await Until(() => host.GetView().Players.Length == 2 && client.GetView().Players.Length == 2);
            var sequence = (uint)(iteration * 10 + 1);
            var damage = Task.Run(async () => { for (var j = 0; j < 5; j++) { host.Fire(1, 0, sequence + (uint)j); await Task.Delay(55); } });
            var disconnect = Task.Run(async () => { await Task.Delay(500); await client.DisposeAsync(); });
            await Task.WhenAll(damage, disconnect).WaitAsync(TimeSpan.FromSeconds(5));
            try { await Until(() => host.GetView().Players.Length == 1); }
            catch (OperationCanceledException)
            {
                var runtime = (RpcRuntime)typeof(ArenaSessionImpl).GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                throw new Exception($"Disconnect cleanup stalled iteration {iteration}, players {host.GetView().Players.Length}, members {string.Join(',', runtime.Members.Select(m => m.Peer.Value))}, error '{host.LastError}'");
            }
            Assert.Equal("", host.LastError);
        }
        Assert.True(host.GetView().TotalHits > 0);
        Assert.True(host.IsReady);
    }

    [Fact] public async Task AcknowledgedLeaveProvesHostRetiredTheAuthenticatedSocket()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port });
        var ticket = await lobby.JoinRoomAsync(guest.SessionToken, host.RoomId);
        using var wire = new TouchSocketClientWire(new PeerId(host.LocalPeerId));
        await wire.ConnectAsync("127.0.0.1", port, ArenaProtocol.GameToken);
        Assert.Contains(guest.PlayerId, await wire.RequestAdmissionAsync(ticket.Ticket));
        await Until(() => host.GetView().Players.Length == 2);
        await wire.NotifyLeavingAsync(); // ACK means host has already removed this session.
        Assert.True(wire.IsConnected);
        Assert.Single(host.GetView().Players);
        Assert.Equal("", host.LastError);
    }

    [Fact] public async Task AbruptSocketDisposalRemovesPeerThroughPhysicalTcpClosedPlugin()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port });
        var ticket = await lobby.JoinRoomAsync(guest.SessionToken, host.RoomId);
        var wire = new TouchSocketClientWire(new PeerId(host.LocalPeerId));
        await wire.ConnectAsync("127.0.0.1", port, ArenaProtocol.GameToken);
        Assert.Contains(guest.PlayerId, await wire.RequestAdmissionAsync(ticket.Ticket));
        await Until(() => host.GetView().Players.Length == 2);
        wire.Dispose(); // No application leave frame: only the physical socket closes.
        await Until(() => host.GetView().Players.Length == 1);
        Assert.Equal("", host.LastError);
    }

    [Fact] public async Task FailedSendToStillActivePeerIsReported()
    {
        using var wire = new FailingWire();
        using var runtime = new RpcRuntime(NetworkRole.Host, "scope", new PeerId("host"), new PeerId("host"), wire);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        runtime.UnhandledDispatch += errors.Enqueue;
        runtime.RegisterMember(new RoomMember(new PeerId("active")));
        await Until(() => errors.Any());
        Assert.Equal(RpcError.Disconnected, Assert.IsType<RpcException>(errors.Single()).Error);
    }

    private sealed class FailingWire : IRoomWire
    {
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received { add { } remove { } }
        public event Action<PeerId>? PeerLeft { add { } remove { } }
        public Task SendAsync(PeerId peer, byte[] data) => Task.FromException(new RpcException(RpcError.Disconnected, "Active peer send failed"));
        public void Dispose() { }
    }

    [Fact] public async Task FailedTickWithdrawsHostAndClosesRoom()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port });
        var authority = ((ArenaSessionImpl)host).Authority!;
        var bullets = (List<BulletPose>)typeof(ArenaAuthority).GetField("Bullets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(authority)!;
        lock (authority.Gate) bullets.Add(null!); // Deliberate fatal tick fault; not an input a player can send.
        await Until(() => !host.IsReady && lobby.RoomClosed);
        Assert.NotEmpty(host.LastError);
    }

    [Fact] public async Task TimedOutAdmissionReleasesLateRedemptionAndReportsSanitizedError()
    {
        int port = GameTestPorts.FreeTcpUdpPort();
        using var host = new TouchSocketHostWire();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = 0; string? failure = null;
        host.AdmissionFailed += reason => failure = reason;
        await host.StartAsync(port, ArenaProtocol.GameToken, IPAddress.Loopback,
            async _ => { await release.Task; return (new PeerId("late-peer"), "{\"Success\":true}"); },
            _ => throw new Exception("Should not admit after timeout"), _ => Interlocked.Increment(ref aborted));
        using var client = new TouchSocketClientWire(new PeerId("host"));
        await client.ConnectAsync("127.0.0.1", port, ArenaProtocol.GameToken);
        Assert.Contains("Admission timed out", await client.RequestAdmissionAsync("secret"));
        Assert.Equal("Admission timed out", failure);
        release.SetResult();
        await Until(() => Volatile.Read(ref aborted) == 1);
        Assert.True(host.IsConnected); // Host survives a rejected connection.
    }

    [Fact] public async Task BroadcastsWaitForActualSocketDirectoryAcknowledgement()
    {
        var lobby = new FakeLobby(); var hostAuth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Slow client");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth, new ArenaHostOptions { Port = port });
        var ticket = await lobby.JoinRoomAsync(guest.SessionToken, host.RoomId);
        using var wire = new TouchSocketClientWire(new PeerId(host.LocalPeerId));
        var prematureCalls = 0;
        wire.Received += (_, data) => { if (data.Length >= 3 && data[0] == 0xB6 && data[1] == 4 && data[2] is 1 or 2) Interlocked.Increment(ref prematureCalls); }; // Typed v4 reliable notification/request, not cold v3 reply.
        await wire.ConnectAsync("127.0.0.1", port, ArenaProtocol.GameToken);
        var admission = await wire.RequestAdmissionAsync(ticket.Ticket);
        Assert.Contains(guest.PlayerId, admission);
        var runtime = (RpcRuntime)typeof(ArenaSessionImpl).GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await Until(() => runtime.Members.Count == 2);
        Assert.False(runtime.Members.Single(m => m.PlayerId == guest.PlayerId).Ready);
        await wire.NotifyPreparedAsync();
        await Until(() => runtime.Members.Single(m => m.PlayerId == guest.PlayerId).Ready);
        // Host sees the member as ready, but must withhold All calls until the client
        // processes its ready directory and explicitly acknowledges it on this socket.
        await Task.Delay(5600);
        Assert.Equal(0, Volatile.Read(ref prematureCalls));
        Assert.Equal("", host.LastError);
        await wire.NotifyLeavingAsync();
        await Until(() => host.GetView().Players.Length == 1);
    }

    [Fact] public async Task RemoteLobbyAndGameHostSynchronizeClientReadiness()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var lobbyPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        int gamePort = GameTestPorts.FreeTcpUdpPort();
        var bin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.FullName;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var lobbyPath = Path.Combine(bin, "Arena.Lobby", configuration, "net10.0", "Arena.Lobby.dll");
        Assert.True(File.Exists(lobbyPath), "Lobby executable project reference must be built before this test.");
        var ready = Path.Combine(Path.GetTempPath(), "arena-lobby-ready-" + Guid.NewGuid().ToString("N") + ".json");
        using var process = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { lobbyPath, "--port", lobbyPort.ToString(), "--ready-file", ready }, UseShellExecute = false })!;
        try
        {
            await Until(() => File.Exists(ready));
            await using var lobby = await ArenaLobbyClient.ConnectAsync("127.0.0.1", lobbyPort);
            var hostAuth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Client");
            await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth, new ArenaHostOptions { Port = gamePort, LocalPlayer = false });
            await using var client = await ArenaSession.StartClientAsync(lobby, guest, new ArenaClientOptions { RoomId = host.RoomId });
            await Until(() => client.IsReady && host.GetView().Players.Length == 1 && client.GetView().Players.Length == 1);
            await Task.Delay(300);
            Assert.Equal("", host.LastError);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (File.Exists(ready)) File.Delete(ready);
        }
    }

    [Fact] public async Task PreAdmissionPoseCannotTombstoneAnAdmittedHealthBinding()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port, LocalPlayer = false });
        await using var client = await ArenaSession.StartClientAsync(lobby, guest, new ArenaClientOptions { RoomId = host.RoomId });
        var game = (ArenaSessionImpl)client;
        game.ReceivePlayerMotion(1000000, new ArraySegment<PlayerMotionUpdate>(Array.Empty<PlayerMotionUpdate>()));
        var snapshot = host.GetView(); snapshot.Tick = 1000001;
        typeof(ArenaSessionImpl).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, new object[] { snapshot });
        Assert.Equal(guest.PlayerId, Assert.Single(client.GetView().Players).PlayerId);
        Assert.Equal(100, client.GetView().Players[0].Health); // Still the woven state object.
    }

    [Fact] public async Task UdpPauseFreezesPositionButReliableProbeAndHealthRemainLiveAndRebindKeepsPeer()
    {
        var lobby = new FakeLobby(); var hostAuth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, hostAuth, new ArenaHostOptions { Port = port });
        await using var client = await ArenaSession.StartClientAsync(lobby, guest, new ArenaClientOptions { RoomId = host.RoomId });
        await Until(() => client.GetDatagramReport().PlayerBatchesReceived > 0);
        string peer = client.LocalPeerId;
        client.SetUdpEnabled(false);
        await Task.Delay(300);
        long tick = client.GetDatagramReport().PositionTick;
        long probes = client.GetDatagramReport().ReliableProbesCompleted;
        host.Fire(1, 0, 1);
        await Until(() => client.GetView().Players.Single(p => p.PeerId == peer).Health == 75 && client.GetDatagramReport().ReliableProbesCompleted > probes);
        Assert.Equal(tick, client.GetDatagramReport().PositionTick);
        Assert.True(client.GetDatagramReport().HealthUpdatesReceived > 0);
        client.SetUdpEnabled(true);
        await client.RebindUdpAsync();
        Assert.Equal(peer, client.LocalPeerId);
        try { await Until(() => client.GetDatagramReport().PositionTick > tick); }
        catch (OperationCanceledException)
        {
            var h = host.GetDatagramReport(); var c = client.GetDatagramReport();
            throw new Exception($"Direct rebind recovery stalled: hostReady={h.Ready} hostSent={h.SentDatagrams} " +
                $"clientReady={c.Ready} clientReceived={c.ReceivedDatagrams} tick={c.PositionTick}/{tick} " +
                $"hostError={host.LastError} clientError={client.LastError}");
        }
    }

    [Fact] public async Task PerEntityTickRejectsReorderedAndUnknownUpdates()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host"); var guest = await lobby.GuestAsync("Guest");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port });
        await using var client = await ArenaSession.StartClientAsync(lobby, guest, new ArenaClientOptions { RoomId = host.RoomId });
        client.SetUdpEnabled(false);
        await Task.Delay(200);
        var known = client.GetView().Players[0];
        var updates = new[] { new PlayerMotionUpdate { Id = known.MotionId, X = 3, AimX = 1 }, new PlayerMotionUpdate { Id = int.MaxValue, X = 9 } };
        long tick = client.GetView().Tick + 1000;
        ((ArenaSessionImpl)client).ReceivePlayerMotion(tick, updates);
        ((ArenaSessionImpl)client).ReceivePlayerMotion(tick - 1, new[] { new PlayerMotionUpdate { Id = known.MotionId, X = -8 } });
        ((ArenaSessionImpl)client).ReceivePlayerMotion(tick, new[] { new PlayerMotionUpdate { Id = known.MotionId, X = -7 } });
        Assert.Equal(3, client.GetView().Players.Single(p => p.MotionId == known.MotionId).X);
        Assert.Equal(2, client.GetView().Players.Length);
    }

    [Fact] public async Task DedicatedHostHasNoSyntheticLocalPlayer()
    {
        var lobby = new FakeLobby(); var auth = await lobby.GuestAsync("Host");
        int port = GameTestPorts.FreeTcpUdpPort();
        await using var host = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions { Port = port, LocalPlayer = false });
        Assert.Empty(host.GetView().Players); Assert.Empty(host.LocalPlayerId);
        Assert.True(host.IsReady);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(25, timeout.Token); }
    }

    private sealed class FakeLobby : IArenaLobbyApi
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AuthSession> _sessions = new();
        private readonly Dictionary<string, AdmissionIdentity> _tickets = new();
        private RoomInfo? _room;
        public bool RoomClosed { get; private set; }
        public Task<AuthSession> GuestAsync(string name)
        {
            var auth = new AuthSession { Success = true, PlayerId = Guid.NewGuid().ToString("N"), DisplayName = name, SessionToken = Guid.NewGuid().ToString("N") };
            lock (_gate) _sessions.Add(auth.SessionToken, auth);
            return Task.FromResult(auth);
        }
        public Task<AuthSession> RegisterAsync(string username, string password) => GuestAsync(username);
        public Task<AuthSession> LoginAsync(string username, string password) => GuestAsync(username);
        public Task<RoomRegistration> CreateRoomAsync(string token, string name, string address, int port)
        {
            lock (_gate)
            {
                _room = new RoomInfo { RoomId = Guid.NewGuid().ToString("N"), Name = name, Scope = Guid.NewGuid().ToString("N"), HostPeerId = Guid.NewGuid().ToString("N"), Address = address, Port = port };
                return Task.FromResult(new RoomRegistration { Success = _sessions.ContainsKey(token), Room = _room, HostToken = "host-secret" });
            }
        }
        public Task<RoomInfo[]> ListRoomsAsync(string token) { lock (_gate) return Task.FromResult(_room != null && _sessions.ContainsKey(token) ? new[] { _room } : Array.Empty<RoomInfo>()); }
        public Task<JoinTicket> JoinRoomAsync(string token, string roomId)
        {
            lock (_gate)
            {
                if (_room?.RoomId != roomId || !_sessions.TryGetValue(token, out var auth)) return Task.FromResult(new JoinTicket { Error = "Denied" });
                var ticket = Guid.NewGuid().ToString("N");
                _tickets.Add(ticket, new AdmissionIdentity { Success = true, PlayerId = auth.PlayerId, DisplayName = auth.DisplayName, PeerId = Guid.NewGuid().ToString("N") });
                return Task.FromResult(new JoinTicket { Success = true, Ticket = ticket, Room = _room });
            }
        }
        public Task<AdmissionIdentity> RedeemTicketAsync(string roomId, string hostToken, string ticket)
        {
            lock (_gate)
            {
                if (_room?.RoomId != roomId || hostToken != "host-secret" || !_tickets.Remove(ticket, out var identity)) return Task.FromResult(new AdmissionIdentity { Error = "Denied" });
                return Task.FromResult(identity);
            }
        }
        public Task<bool> HeartbeatRoomAsync(string roomId, string token) => Task.FromResult(true);
        public Task<bool> CloseRoomAsync(string roomId, string token) { RoomClosed = true; return Task.FromResult(true); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
