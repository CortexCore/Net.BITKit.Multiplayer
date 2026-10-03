using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using BITKit.Multiplayer.Samples.Arena;
using BITKit.Multiplayer.TouchSocket;

// Launches real OS processes. Reports are produced independently by each running node.
var visual = args.Contains("--visual");
var compareInterpolation = args.Contains("--compare-interpolation");
var useRelay = args.Contains("--relay");
var profile = args.Contains("--profile");
var transportIndex = Array.IndexOf(args, "--transport");
var transport = transportIndex < 0 ? "native" : transportIndex + 1 < args.Length ? args[transportIndex + 1] : "";
if (transport is not ("native" or "touchsocket")) throw new ArgumentException("--transport must be native or touchsocket.");
var expectedTransportName = transport == "native" ? "native-udp" : "touchsocket-udp";
var allocationPhases = profile || args.Contains("--allocation-phases");
var killRelay = args.Contains("--kill-relay");
var killHost = args.Contains("--kill-host");
var pauseUdp = args.Contains("--pause-udp");
var rebindUdp = args.Contains("--rebind-udp");
if ((killRelay || killHost) && !useRelay || killRelay && killHost)
    throw new ArgumentException("Use --relay with one failure scenario: --kill-relay or --kill-host.");
if (pauseUdp && !useRelay || (killRelay || killHost) && (pauseUdp || rebindUdp))
    throw new ArgumentException("--pause-udp requires --relay; UDP diagnostics cannot be combined with route-loss scenarios.");
if (profile && (killRelay || killHost))
    throw new ArgumentException("--profile requires a full-shutdown scenario, not --kill-relay or --kill-host.");
int Option(string key, int fallback, int maximum)
{
    var index = Array.IndexOf(args, key);
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < 0 || value > maximum)
        throw new ArgumentException($"{key} requires a value between 0 and {maximum}.");
    return value;
}
var poseDelay = Option("--pose-delay-ms", 0, 1000);
var poseJitter = Option("--pose-jitter-ms", 0, 500);
var soakSeconds = Option("--soak-seconds", 0, 300);
if (soakSeconds > 0 && (visual || killRelay || killHost))
    throw new ArgumentException("--soak-seconds requires the normal headless full-shutdown scenario.");
int bobDuration = Math.Max(profile ? 75 : 32, soakSeconds);
int aliceDuration = Math.Max(45, bobDuration + 12);
int charlieDuration = Math.Max(36, bobDuration + 4);
int hostDuration = Math.Max(55, bobDuration + 22);
var directory = new DirectoryInfo(AppContext.BaseDirectory);
while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Net.BITKit.Multiplayer.slnx"))) directory = directory.Parent;
if (directory == null) throw new InvalidOperationException("Run from a repository build output.");
var root = directory.FullName;
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var run = Path.Combine(root, "Artifacts", "ArenaRuns", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(run);
var processes = new List<Node>();
var observations = new List<string>();
ProfileSnapshot? profileStartSnapshot = null;
var app = Path.Combine(root, "Artifacts", "bin", "Arena.App", configuration, "net10.0", "Arena.App.dll");
var lobbyDll = Path.Combine(root, "Artifacts", "bin", "Arena.Lobby", configuration, "net10.0", "Arena.Lobby.dll");
var relayDll = Path.Combine(root, "Artifacts", "bin", "Arena.Relay", configuration, "net10.0", "Arena.Relay.dll");
var profilerDll = Path.Combine(root, "Artifacts", "bin", "Arena.Performance", configuration, "net10.0", "Arena.Performance.dll");
// A no-restore build after a runtime package change can produce a runnable App
// missing its codec dependency. Fail before spawning peers instead of reporting
// a misleading admission timeout when the dedicated Host's first tick crashes.
if (!File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "MemoryPack.Core.dll")))
    throw new InvalidOperationException("Arena.App lacks MemoryPack.Core.dll; restore the Arena.E2E project graph and rebuild the App.");
TcpListener? blockedDirectPort = null;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120 + soakSeconds));
string FileFor(string name) => Path.Combine(run, name);
int FreePort()
{
    SocketException? failure = null;
    for (int attempt = 0; attempt < 32; attempt++)
    {
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            return port;
        }
        catch (SocketException ex) { failure = ex; } // Retry only a failed probe, not a spawned scenario.
    }
    throw new InvalidOperationException("No loopback port available to both TCP and UDP (probe only).", failure);
}
Node Start(string name, string dll, params string[] arguments)
{
    if (!File.Exists(dll)) throw new FileNotFoundException("Build the Arena solution first.", dll);
    var node = new Node(name, dll, root, FileFor(name + ".log"), arguments);
    processes.Add(node); return node;
}
ProfileSnapshot CaptureProfileSnapshot()
{
    var at = DateTimeOffset.UtcNow;
    var reports = new[] { "host", "alice", "bob", "charlie" }.ToDictionary(n => n, n =>
    {
        var path = FileFor(n + ".json");
        // Never rehydrate get-only ArenaAllocationPhaseTotals: System.Text.Json creates
        // fresh zero-valued counters. Clone both values from the SAME open report file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var gc = root.GetProperty("Gc").GetProperty("Final").Clone();
        var phases = root.GetProperty("Allocations").Clone();
        if (gc.ValueKind != JsonValueKind.Object || phases.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(n + " lacks raw GC or allocation phase counters.");
        // The pathname may have been atomically replaced after opening the stream;
        // this timestamp is approximate, not an exact timestamp for the parsed bytes.
        return new ProfileNodeSnapshot(File.GetLastWriteTimeUtc(path), root.GetProperty("TransportName").GetString() ?? "", gc, phases);
    });
    return new ProfileSnapshot(at, reports);
}
void VerifyPhaseSnapshot(ProfileSnapshot snapshot, bool after)
{
    foreach (var (name, entry) in snapshot.Reports)
    {
        Require(entry.TransportName == expectedTransportName,
            name + " profile window selected a different UDP transport factory.");
        var phases = entry.AllocationPhases;
        Require(phases.GetProperty("Sampling").GetBoolean() && phases.GetProperty("ViewClone").GetProperty("Count").GetInt64() > 0,
            name + " allocation phase sampling was not active in the raw report snapshot.");
        if (!after) continue;
        var before = profileStartSnapshot!.Reports[name].AllocationPhases;
        foreach (var phase in name == "host"
                     ? new[] { "ViewClone", "Interpolation", "ReportWrite", "MotionSend" }
                     : new[] { "ViewClone", "Interpolation", "ReportWrite" })
            Require(phases.GetProperty(phase).GetProperty("Count").GetInt64() > before.GetProperty(phase).GetProperty("Count").GetInt64() &&
                    phases.GetProperty(phase).GetProperty("Bytes").GetInt64() > before.GetProperty(phase).GetProperty("Bytes").GetInt64(),
                name + " " + phase + " did not advance in the raw phase-window snapshots.");
    }
}
object[] ProfileArtifactHashes()
{
    var appDir = Path.GetDirectoryName(app)!;
    return new[] { "Arena.App.dll", "TouchSocket.dll", "Net.BITKit.Multiplayer.TouchSocket.dll", "Net.BITKit.Multiplayer.Transport.dll" }
        .Select(name => Path.Combine(appDir, name))
        .Where(File.Exists)
        .Select(path => { using var stream = File.OpenRead(path);
            return (object)new { Path = path, Version = FileVersionInfo.GetVersionInfo(path).FileVersion,
                Sha256 = Convert.ToHexString(SHA256.HashData(stream)) }; }).ToArray();
}
void Passed(string message) { observations.Add(message); Console.WriteLine("PASS " + message); }
async Task Wait(string description, Func<bool> condition, TimeSpan? timeout = null)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        foreach (var node in processes.Where(n => n.Name is "host" or "alice" or "bob" or "charlie"))
        {
            var report = ReadReport(node.Name);
            if (!node.ExpectedExit && !string.IsNullOrEmpty(report?.Error))
                throw new InvalidOperationException(node.Name + " reported " + report.Error + "; logs: " + run);
            if (node.Process.HasExited && !node.ExpectedExit)
                throw new InvalidOperationException(node.Name + " exited early (code " + node.Process.ExitCode + ") during " + description + "; logs: " + run);
        }
        if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(12)))
            throw new TimeoutException(description + "; logs: " + run);
        await Task.Delay(50, deadline.Token);
    }
}
ArenaReport? ReadReport(string name)
{
    try
    {
        var path = FileFor(name + ".json");
        if (!File.Exists(path)) return null;
        // Reports are atomically replaced by another process. On Windows the reader must
        // allow delete/rename, otherwise merely observing a report can crash its writer.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<ArenaReport>(stream);
    }
    catch (IOException) { return null; }
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
bool LogContains(string name, string marker)
{
    try
    {
        using var stream = new FileStream(FileFor(name + ".log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Contains(marker, StringComparison.Ordinal);
    }
    catch (IOException) { return false; }
}
async Task WriteUdpControl(string value)
{
    var path = FileFor("relay.udp-control");
    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    File.WriteAllText(temporary, value);
    try
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { File.Move(temporary, path, true); return; }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(30, deadline.Token); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(30, deadline.Token); }
        }
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}
RelayStatistics? RelayStats()
{
    try
    {
        var path = FileFor("relay.json");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<RelayStatistics>(stream);
    }
    catch (IOException) { return null; }
}

try
{
    Console.WriteLine("Arena E2E artifacts: " + run);
    var lobbyPort = FreePort(); var gamePort = FreePort(); var relayPort = FreePort();
    var lobbyNode = Start("lobby", lobbyDll, "--port", lobbyPort.ToString(), "--ready-file", FileFor("lobby.ready.json"));
    await Wait("Lobby startup", () => File.Exists(FileFor("lobby.ready.json")));
    await using var lobby = await ArenaLobbyClient.ConnectAsync("127.0.0.1", lobbyPort, deadline.Token);
    var name = "test" + Guid.NewGuid().ToString("N")[..10];
    var password = Guid.NewGuid().ToString("N") + "a!";
    var account = await lobby.RegisterAsync(name, password);
    Require(account.Success, "Registration failed: " + account.Error);
    var login = await lobby.LoginAsync(name, password);
    Require(login.Success && login.PlayerId == account.PlayerId, "Login did not preserve account identity.");
    Require(!(await lobby.LoginAsync(name, "incorrect-password")).Success, "Wrong password was accepted.");
    var guest = await lobby.GuestAsync("E2E Observer");
    Require(guest.Success && guest.PlayerId != login.PlayerId, "Guest identity not distinct.");
    Passed("separate Lobby process: registration, login, bad-password denial, guest identity");

    Node? relayNode = null;
    object? pauseEvidence = null, rebindEvidence = null;
    if (useRelay)
    {
        var relayArgs = new List<string> { "--transport", transport, "--port", relayPort.ToString(), "--lobby-port", lobbyPort.ToString(),
            "--ready-file", FileFor("relay.ready.json"), "--report", FileFor("relay.json") };
        if (pauseUdp)
        {
            await WriteUdpControl("on");
            relayArgs.AddRange(["--udp-control-file", FileFor("relay.udp-control")]);
        }
        relayNode = Start("relay", relayDll, relayArgs.ToArray());
        await Wait("Relay startup", () => File.Exists(FileFor("relay.ready.json")));
        using (var relayReady = JsonDocument.Parse(File.ReadAllText(FileFor("relay.ready.json"))))
        Require(relayReady.RootElement.GetProperty("TransportName").GetString() == expectedTransportName,
            "Relay did not resolve the requested UDP transport factory.");
        blockedDirectPort = new TcpListener(IPAddress.Loopback, gamePort) { ExclusiveAddressUse = true };
        blockedDirectPort.Start(); // A Relay Host cannot possibly open the configured Direct game listener.
        Passed("Relay process ready; Host direct game port deliberately occupied");
    }

    var hostArgs = new List<string> { "--role", "host", "--transport", transport, "--headless", "--no-local-player", "--name", "Authority",
        "--lobby-port", lobbyPort.ToString(), "--game-port", gamePort.ToString(), "--duration", hostDuration.ToString(),
        "--route", useRelay ? "relay" : "direct", "--relay-port", relayPort.ToString(),
        "--report", FileFor("host.json"), "--ready-file", FileFor("host.ready.json") };
    if (allocationPhases) hostArgs.Add("--allocation-phases");
    var host = Start("host", app, hostArgs.ToArray());
    await Wait("Host startup", () => ReadReport("host")?.WasReady == true);
    var roomId = ReadReport("host")!.RoomId;
    var rooms = await lobby.ListRoomsAsync(guest.SessionToken);
    Require(rooms.Any(r => r.RoomId == roomId), "Live Host was not listed by Lobby.");
    Require(rooms.Single(r => r.RoomId == roomId).ConnectionMode == (useRelay ? ArenaConnectionMode.Relay : ArenaConnectionMode.Direct),
        "Lobby published the wrong connection route.");
    Passed("separate dedicated Host process advertises room and becomes ready");

    Node Client(string file, string displayName, string bot, string duration, string view, bool graphics)
    {
        var command = new List<string> { "--role", "client", "--transport", transport, "--name", displayName, "--lobby-port", lobbyPort.ToString(),
            "--room", roomId, "--bot", bot, "--duration", duration, "--view", view,
            "--pose-delay-ms", poseDelay.ToString(), "--pose-jitter-ms", poseJitter.ToString(),
            "--report", FileFor(file + ".json"), "--ready-file", FileFor(file + ".ready.json") };
        if (compareInterpolation && file == "bob") command.Add("--no-interpolation");
        if (allocationPhases) command.Add("--allocation-phases");
        if (rebindUdp && file == "alice") command.AddRange(["--udp-rebind-after", "24"]);
        if (!graphics) command.Add("--headless");
        else
        {
            command.AddRange(new[] { "--capture", FileFor(file + ".png"),
                "--window-x", file == "alice" ? "40" : "1280", "--window-y", "80" });
        }
        return Start(file, app, command.ToArray());
    }
    var alice = Client("alice", "Alice", "shoot", aliceDuration.ToString(), "2d", visual);
    await Wait("Alice startup", () => ReadReport("alice")?.WasReady == true);
    var bob = Client("bob", "Bob", "idle", bobDuration.ToString(), "3d", visual);
    await Wait("Bob startup", () => ReadReport("bob")?.WasReady == true);
    var bobId = ReadReport("bob")!.PlayerId;
    var aliceId = ReadReport("alice")!.PlayerId;
    Require(ReadReport("alice")!.PeerId != ReadReport("bob")!.PeerId, "Clients share a PeerId.");
    await Wait("Host and both clients see two players", () => new[] { "host", "alice", "bob" }.All(n => ReadReport(n)?.Final.Players.Length == 2));
    Require(ReadReport("host")!.PlayerId == "", "Dedicated Host unexpectedly spawned a local client/player.");
    Passed("two separate Client processes admitted; all nodes agree on membership; no synthetic Host client");
    await Wait("Host UDP sends and both clients receive position batches", () =>
        ReadReport("host")?.Datagrams is { Ready: true, Enabled: true, SentDatagrams: > 0, PlayerBatchesReceived: > 0 } &&
        new[] { "alice", "bob" }.All(n => ReadReport(n)?.Datagrams is
            { Ready: true, Enabled: true, ReceivedDatagrams: > 0, PlayerBatchesReceived: > 0 }));
    var expectedPeers = new[] { ReadReport("alice")!.PeerId, ReadReport("bob")!.PeerId }.Order().ToArray();
    foreach (var node in new[] { "host", "alice", "bob" })
    {
        var report = ReadReport(node)!;
        Require(report.TransportName == expectedTransportName,
            node + " did not resolve the requested UDP transport factory.");
        Require(report.Final.Scope == ReadReport("host")!.Final.Scope && !string.IsNullOrEmpty(report.Final.Scope), node + " has a mismatched scope.");
        Require(report.Final.Players.Select(p => p.PeerId).Order().SequenceEqual(expectedPeers), node + " has an incorrect ready peer map.");
        Require(report.Final.Players.Select(p => p.MotionId).All(id => id > 0) &&
            report.Final.Players.Select(p => p.MotionId).Distinct().Count() == 2, node + " has an invalid scoped MotionId map.");
        Require(report.Datagrams.LargestDatagramBytes is > 0 and <= 1200 && report.Datagrams.PayloadLimit is > 0 and <= 1200,
            node + " reported oversized UDP datagrams.");
        Require(report.Error.Length == 0, node + " reported a gameplay error while receiving UDP.");
    }
    Passed("scoped ready peers: Host sends real UDP motion batches to both receiving clients; every observed wire datagram <=1200 bytes");
    var initialDatagrams = new[] { "host", "alice", "bob" }.ToDictionary(n => n, n => ReadReport(n)!.Datagrams);
    var aliceBeforeRebind = ReadReport("alice")!;
    await Wait("authoritative hit and SyncVar health", () => ReadReport("host")?.Final.TotalHits > 0 && ReadReport("bob")?.MinimumOwnHealth < 100);
    var hitCount = ReadReport("host")!.Final.TotalHits;
    Passed("woven Client input -> Host bullet simulation -> All broadcast -> Client SyncVar health decrease");

    if (useRelay)
    {
        using (var relayReady = JsonDocument.Parse(File.ReadAllText(FileFor("relay.ready.json"))))
            Require(relayReady.RootElement.GetProperty("TransportName").GetString() == ReadReport("host")!.TransportName,
                "Relay and Host selected different UDP transport factories.");
        await Wait("Relay forwards real gameplay", () => RelayStats() is { ForwardedMessages: > 0, ForwardedBytes: > 0, Clients: 2 });
        foreach (var node in new[] { "host", "alice", "bob" })
        {
            var report = ReadReport(node)!;
            Require(report.ConnectionMode == ArenaConnectionMode.Relay && report.ConnectedEndpoint.EndsWith(":" + relayPort),
                node + " did not establish the Relay route.");
        }
        Require(blockedDirectPort != null && !blockedDirectPort.Pending(), "A client attempted forbidden direct fallback.");
        await Wait("Relay forwards UDP gameplay", () => RelayStats() is { UdpForwardedDatagrams: > 0, UdpForwardedBytes: > 0, LargestUdpDatagramBytes: > 0 and <= 1200 });
        Passed("all actual endpoints point to Relay; real forwarding counters increase; no Direct attempts");
    }

    if (pauseUdp)
    {
        var beforePause = ReadReport("bob")!.Datagrams;
        var relayBefore = RelayStats()!;
        await WriteUdpControl("off");
        await Wait("Relay accepted local UDP pause", () => RelayStats()?.UdpForwardingEnabled == false);
        await Wait("Paused Relay drops gameplay UDP", () => RelayStats()?.UdpDroppedDatagrams > relayBefore.UdpDroppedDatagrams);
        await Task.Delay(650, deadline.Token); // drain the last in-flight packet and two 250ms report writes
        var paused = ReadReport("bob")!;
        Require(paused.Datagrams.Ready && paused.Datagrams.Enabled, "UDP pause disabled binding or the client application lane.");
        long frozenTick = paused.Datagrams.PositionTick;
        long probesBefore = paused.Datagrams.ReliableProbesCompleted;
        long healthBefore = paused.Datagrams.HealthUpdatesReceived;
        long hitsBefore = ReadReport("host")!.Final.TotalHits;
        await Wait("Reliable tick probes AND actual Health SyncVar changes while UDP frozen", () =>
        {
            var current = ReadReport("bob");
            if (current == null) return false;
            if (current.Datagrams.PositionTick != frozenTick) throw new InvalidOperationException("UDP position advanced during Relay UDP pause.");
            return current.Datagrams.ReliableProbesCompleted > probesBefore &&
                   current.Datagrams.HealthUpdatesReceived > healthBefore &&
                   ReadReport("host")?.Final.TotalHits > hitsBefore;
        }, TimeSpan.FromSeconds(7));
        await Task.Delay(550, deadline.Token);
        var atResume = ReadReport("bob")!;
        Require(atResume.Datagrams.PositionTick == frozenTick && atResume.IsReady && atResume.Error.Length == 0,
            "UDP pause changed motion or lost the reliable session.");
        await WriteUdpControl("on");
        await Wait("Relay resumes UDP forwarding", () => RelayStats()?.UdpForwardingEnabled == true);
        await Wait("PositionTick resumes over UDP", () => ReadReport("bob")?.Datagrams.PositionTick > frozenTick);
        var recovered = ReadReport("bob")!;
        pauseEvidence = new { Before = beforePause, Paused = paused.Datagrams, FrozenTick = frozenTick,
            ReliableProbesBefore = probesBefore, HealthUpdatesBefore = healthBefore,
            AtResume = atResume.Datagrams, Recovered = recovered.Datagrams,
            RelayBefore = relayBefore, RelayAfter = RelayStats() };
        Passed("Relay UDP alone paused: motion froze; reliable probes, Host hits and woven Health progressed; binding stayed ready; UDP resumed");
    }

    if (killRelay || killHost)
    {
        var victim = killRelay ? relayNode! : host;
        host.ExpectedExit = alice.ExpectedExit = bob.ExpectedExit = true; // intentional route loss
        victim.Process.Kill(entireProcessTree: true); await victim.Process.WaitForExitAsync(deadline.Token);
        await Wait("Route loss revokes readiness", () => ReadReport("alice") is { IsReady: false } && ReadReport("bob") is { IsReady: false }, TimeSpan.FromSeconds(22));
        await Wait("Disconnected presentation clears", () => ReadReport("alice")!.Presentation.Frame.Players.Length == 0 &&
            ReadReport("bob")!.Presentation.Frame.Players.Length == 0);
        var stoppedTick = ReadReport("alice")!.Final.Tick;
        await Task.Delay(500, deadline.Token);
        Require(ReadReport("alice")!.Final.Tick == stoppedTick, "Gameplay continued after the only route disappeared.");
        Require(!blockedDirectPort!.Pending(), "Relay loss triggered a Direct fallback.");
        if (killHost) await Wait("Relay removes the dead Host room", () => RelayStats() is { Rooms: 0, Clients: 0 });
        var closeTimeout = Stopwatch.StartNew();
        while ((await lobby.ListRoomsAsync(guest.SessionToken)).Any(r => r.RoomId == roomId))
        {
            if (closeTimeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Lost Host room remains published.");
            await Task.Delay(100, deadline.Token);
        }
        Passed((killRelay ? "killed Relay" : "killed Host") + ": clients lose readiness, frames clear, room disappears, no direct fallback");
        File.WriteAllText(FileFor("summary.json"), JsonSerializer.Serialize(new { Passed = true, Relay = true, Transport = transport,
            TransportName = ReadReport("host")?.TransportName,
            FailureScenario = killRelay ? "relay-loss" : "host-loss", InitialDatagrams = initialDatagrams,
            Checks = observations, Artifacts = run }));
        Console.WriteLine("ARENA_E2E_PASSED " + run);
        return 0;
    }

    var charlie = Client("charlie", "Charlie", "move", charlieDuration.ToString(), "2d", false);
    await Wait("Late join startup", () => ReadReport("charlie")?.WasReady == true);
    Require(ReadReport("charlie")!.TransportName == expectedTransportName, "Late client did not resolve the requested transport factory.");
    var charlieId = ReadReport("charlie")!.PlayerId;
    await Wait("Late join state", () => ReadReport("charlie")?.Final.Players.Length == 3 && ReadReport("charlie")?.Final.TotalHits >= hitCount);
    var initialPose = ReadReport("charlie")!.Final.Players.Single(p => p.PlayerId == charlieId);
    await Wait("Movement reflected independently on Host and other client", () =>
    {
        var own = ReadReport("charlie")?.Final.Players.FirstOrDefault(p => p.PlayerId == charlieId);
        var authority = ReadReport("host")?.Final.Players.FirstOrDefault(p => p.PlayerId == charlieId);
        var other = ReadReport("alice")?.Final.Players.FirstOrDefault(p => p.PlayerId == charlieId);
        return own != null && authority != null && other != null &&
            MathF.Abs(own.X - initialPose.X) + MathF.Abs(own.Z - initialPose.Z) > 1 &&
            MathF.Abs(authority.X - other.X) + MathF.Abs(authority.Z - other.Z) < 2;
    });
    Passed("late join receives current state and its movement replicates to independent processes");
    Node[] profilers = [];
    if (profile)
    {
        await Wait("Three-player warmup on Host and Alice", () => new[] { "host", "alice", "bob", "charlie" }
            .All(n => ReadReport(n)?.Final.Players.Length == 3 && ReadReport(n)?.Gc.SampleCount >= 5));
        profileStartSnapshot = CaptureProfileSnapshot();
        VerifyPhaseSnapshot(profileStartSnapshot, after: false);
        // Two separate profiler processes are started back-to-back against the same steady-state
        // window. They terminate tracing and write summaries before node teardown.
        profilers = [Start("profile-host", profilerDll, "--pid", host.Process.Id.ToString(), "--duration", "35", "--outfile", FileFor("profile-host.json")),
            Start("profile-alice", profilerDll, "--pid", alice.Process.Id.ToString(), "--duration", "35", "--outfile", FileFor("profile-alice.json"))];
        await Wait("Both EventPipe streams attached", () => profilers.All(n => LogContains(n.Name, "TRACE_STARTED")), TimeSpan.FromSeconds(20));
        Passed("simultaneous 35-second Host and Alice EventPipe sampling after all players ready");
    }
    if (rebindUdp)
    {
        var before = aliceBeforeRebind;
        Require(!LogContains("alice", "UDP_REBIND_FAILED"), "Alice's UDP endpoint rebind failed.");
        await Wait("Client completed actual UDP endpoint rebind", () => LogContains("alice", "UDP_REBIND_OK") ||
            LogContains("alice", "UDP_REBIND_FAILED"), TimeSpan.FromSeconds(25));
        Require(LogContains("alice", "UDP_REBIND_OK") && !LogContains("alice", "UDP_REBIND_FAILED"), "Alice's UDP endpoint rebind failed.");
        // The latest report may predate the completion marker. Require a newer motion tick,
        // not merely progress since startup. Socket-level counters reset on endpoint replacement.
        var tickAtCompletion = ReadReport("alice")!.Datagrams.PositionTick;
        await Wait("UDP traffic resumes after endpoint rebind", () => ReadReport("alice")?.Datagrams is { Ready: true } udp &&
            udp.PositionTick > tickAtCompletion && udp.ReceivedDatagrams > 0);
        var after = ReadReport("alice")!;
        Require(after.PeerId == before.PeerId && after.PlayerId == before.PlayerId && after.Final.Scope == before.Final.Scope,
            "UDP rebind changed the authenticated peer/player/scope.");
        Require(after.Error.Length == 0 && after.Datagrams.LargestDatagramBytes <= 1200, "UDP rebind reported an error or oversized frame.");
        rebindEvidence = new { Before = before.Datagrams, TickAtCompletion = tickAtCompletion, After = after.Datagrams, PeerIdentityStable = true,
            CompletionMarker = "UDP_REBIND_OK" };
        Passed("client completed real UDP rebind; same peer/player/scope, bound lane and advancing UDP batches");
    }
    await Wait("display interpolation advances independently of 20 Hz snapshots", () =>
        ReadReport("alice")?.Presentation.InterpolatedFrames >= 20 &&
        ReadReport("charlie")?.Presentation.InterpolatedFrames >= 20);
    foreach (var node in new[] { "alice", "bob", "charlie" })
    {
        var report = ReadReport(node)!;
        Require(report.Presentation.SimulatedDelayMilliseconds == poseDelay &&
            report.Presentation.SimulatedJitterMilliseconds == poseJitter, "Pose simulation options did not reach " + node);
        Require(report.Presentation.BufferedSnapshots <= 32 && report.Presentation.PendingSnapshots <= 32, "Unbounded display buffers.");
        Require(report.Presentation.Frame.Players.Select(p => p.PlayerId).Order().SequenceEqual(report.Final.Players.Select(p => p.PlayerId).Order()),
            "Display pose queue changed authoritative membership.");
        foreach (var player in report.Final.Players)
            Require(report.Presentation.Frame.Players.Single(p => p.PlayerId == player.PlayerId).Health == player.Health,
                "Interpolation delayed or changed authoritative Health.");
    }
    if (compareInterpolation)
        Require(!ReadReport("bob")!.Presentation.Enabled && ReadReport("bob")!.Presentation.InterpolatedFrames == 0,
            "Direct comparison mode is still interpolating.");
    Passed("presentation interpolation/direct comparison and bounded pose delay/jitter preserve authority and Health");
    await Wait("Death and respawn", () => ReadReport("bob")?.MinimumOwnHealth == 0 &&
        ReadReport("host")?.Final.Players.Any(p => p.PlayerId == bobId && p.Deaths > 0 && p.Health > 0) == true);
    Passed("authoritative death and respawn restore replicated health");

    if (visual)
    {
        await Wait("2D and 3D framebuffer captures", () => File.Exists(FileFor("alice.png")) && File.Exists(FileFor("bob.png")));
        Require(new FileInfo(FileFor("alice.png")).Length > 1000 && new FileInfo(FileFor("bob.png")).Length > 1000, "Empty graphical capture.");
        Passed("Raylib 2D and 3D windows rendered and saved framebuffer captures (visual inspection separate)");
    }

    if (profile)
    {
        await Wait("Both EventPipe measurement windows ended", () =>
            profilers.All(n => LogContains(n.Name, "TRACE_STOP_REQUESTED")), TimeSpan.FromSeconds(85));
        var profileEndSnapshot = CaptureProfileSnapshot();
        VerifyPhaseSnapshot(profileEndSnapshot, after: true);
        await Wait("Profiler traces stopped and summaries converted before peer exits", () =>
            profilers.All(n => n.Process.HasExited), TimeSpan.FromSeconds(100));
        foreach (var profiler in profilers)
        {
            Require(profiler.Process.ExitCode == 0 && File.Exists(FileFor(profiler.Name + ".json")) &&
                File.Exists(FileFor(profiler.Name + ".nettrace")), profiler.Name + " failed: " + FileFor(profiler.Name + ".log"));
            using var profileSummary = JsonDocument.Parse(File.ReadAllText(FileFor(profiler.Name + ".json")));
            var result = profileSummary.RootElement;
            Require(result.GetProperty("AllocationSamples").GetInt64() > 0 &&
                result.GetProperty("SamplesWithoutResolvedStack").GetInt64() < result.GetProperty("AllocationSamples").GetInt64(),
                profiler.Name + " produced no resolved allocation stacks.");
        }
        var bounds = profilers.ToDictionary(n => n.Name, n =>
        {
            using var summaryDocument = JsonDocument.Parse(File.ReadAllText(FileFor(n.Name + ".json")));
            return new { StartedUtc = summaryDocument.RootElement.GetProperty("StartedUtc").GetDateTimeOffset(),
                StopRequestedUtc = summaryDocument.RootElement.GetProperty("StopRequestedUtc").GetDateTimeOffset() };
        });
        File.WriteAllText(FileFor("profile-window.json"), JsonSerializer.Serialize(new {
            Transport = transport, TransportName = ReadReport("host")?.TransportName,
            Semantics = "Gc.Final and disjoint per-thread phase counters are raw JSON clones from one atomic report read per node, captured before attach and after both TRACE_STOP_REQUESTED markers, not exact EventPipe boundaries. Arena reports refresh about every 250ms. ReportLastWriteUtc is an approximate pathname timestamp, not necessarily the opened file generation; use CapturedUtc and trace bounds to assess staleness. Subtract matching counter fields for approximate phase-window deltas; these exclude receive threads and profiler-process allocations.",
            BeforeAttach = profileStartSnapshot, AfterStopRequest = profileEndSnapshot, TraceBounds = bounds,
            DeployedAppFiles = ProfileArtifactHashes()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Passed("both profilers stopped before game shutdown and saved independent raw traces and JSON summaries");
    }

    bob.ExpectedExit = true;
    await Wait("Bob exits normally", () => bob.Process.HasExited, TimeSpan.FromSeconds(bobDuration + 8));
    Require(bob.Process.ExitCode == 0, "Bob exited with error.");
    await Wait("Disconnected peer removed everywhere", () => new[] { "host", "alice", "charlie" }
        .All(n => ReadReport(n)?.Final.Players.All(p => p.PlayerId != bobId) == true));
    Passed("real client process exit removes player and health binding from remaining peers");
    alice.ExpectedExit = charlie.ExpectedExit = true;
    await Wait("Other clients exit normally", () => alice.Process.HasExited && charlie.Process.HasExited, TimeSpan.FromSeconds(24));
    Require(alice.Process.ExitCode == 0 && charlie.Process.ExitCode == 0, "Client exited with error.");
    await Wait("Host membership clears after clients close", () => ReadReport("host")?.Final.Players.Length == 0);
    host.ExpectedExit = true;
    await Wait("Host exits normally", () => host.Process.HasExited, TimeSpan.FromSeconds(20));
    Require(host.Process.ExitCode == 0, "Host exited with error.");
    Require(!(await lobby.ListRoomsAsync(guest.SessionToken)).Any(r => r.RoomId == roomId), "Closed room is still listed.");
    foreach (var node in new[] { "host", "alice", "bob", "charlie" })
        Require(ReadReport(node)?.Error == "", node + " reported: " + ReadReport(node)?.Error);
    Passed("all game processes finish cleanly and Lobby withdraws the closed room");
    foreach (var node in new[] { "host", "alice", "bob", "charlie" })
    {
        var report = ReadReport(node)!;
        var gc = report.Gc;
        Require(gc.Initial != null && gc.Final != null && gc.PeakManagedHeap != null && gc.SampleCount >= 5 &&
            gc.WindowMilliseconds >= 5000 && gc.TotalAllocatedBytes > 0 &&
            double.IsFinite(gc.AllocatedBytesPerSecond) && double.IsFinite(gc.PeakSampleAllocatedBytesPerSecond) &&
            gc.Final.ElapsedMilliseconds > gc.Initial.ElapsedMilliseconds &&
            gc.PeakManagedHeap.ManagedHeapBytes >= gc.Initial.ManagedHeapBytes &&
            gc.Gen0Collections >= 0 && gc.Gen1Collections >= 0 && gc.Gen2Collections >= 0,
            node + " did not record bounded whole-process GC windows after readiness and warmup.");
    }
    Require(ReadReport("host")!.Datagrams.PoseBufferAllocations == 2 &&
        ReadReport("host")!.Datagrams.PositionSendAllocatedBytes > 0 &&
        ReadReport("bob")!.Datagrams.PlayerBatchesReceived > 0,
        "Motion buffer / position send allocation diagnostics missing from full-session reports.");
    Passed("all processes reported bounded post-warmup whole-process allocation, collection and heap windows alongside motion-send diagnostics");
    if (useRelay)
    {
        await Wait("Relay cleans all routing after Host exits", () => RelayStats() is { Rooms: 0, Clients: 0, PendingAdmissions: 0 });
        Require(!blockedDirectPort!.Pending(), "Unexpected direct connection attempt.");
        Passed("Relay routing and admission tables empty after room shutdown");
    }
    var summary = new { Passed = true, Relay = useRelay, Transport = transport, TransportName = ReadReport("host")?.TransportName,
        RelayStats = useRelay ? RelayStats() : null, Visual = visual, CompareInterpolation = compareInterpolation,
        SoakSeconds = soakSeconds, Profile = profile, AllocationPhases = allocationPhases,
        ProfileWindow = profile ? FileFor("profile-window.json") : null,
        Profiles = profile ? new[] { "profile-host", "profile-alice" }.ToDictionary(n => n, n => FileFor(n + ".json")) : null,
        PauseUdp = pauseUdp, RebindUdp = rebindUdp, PauseEvidence = pauseEvidence, RebindEvidence = rebindEvidence,
        InitialDatagrams = initialDatagrams,
        DatagramReports = new[] { "host", "alice", "bob", "charlie" }.ToDictionary(n => n, n => ReadReport(n)?.Datagrams),
        GcReports = new[] { "host", "alice", "bob", "charlie" }.ToDictionary(n => n, n => ReadReport(n)?.Gc),
        PoseDelayMilliseconds = poseDelay, PoseJitterMilliseconds = poseJitter, Artifacts = run, Checks = observations,
        Pids = processes.Select(n => new { n.Name, Id = n.Process.Id }).ToArray() };
    File.WriteAllText(FileFor("summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("ARENA_E2E_PASSED " + run);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("ARENA_E2E_FAILED " + error.Message);
    File.WriteAllText(FileFor("summary.json"), JsonSerializer.Serialize(new { Passed = false, Error = error.Message,
        Transport = transport, TransportName = ReadReport("host")?.TransportName,
        Checks = observations, Artifacts = run,
        DatagramReports = processes.Where(n => n.Name is "host" or "alice" or "bob" or "charlie")
            .ToDictionary(n => n.Name, n => ReadReport(n.Name)?.Datagrams),
        GcReports = processes.Where(n => n.Name is "host" or "alice" or "bob" or "charlie")
            .ToDictionary(n => n.Name, n => ReadReport(n.Name)?.Gc),
        RelayStats = useRelay ? RelayStats() : null }));
    return 1;
}
finally
{
    foreach (var node in processes.AsEnumerable().Reverse()) node.Dispose();
    blockedDirectPort?.Stop();
}

sealed class Node : IDisposable
{
    private readonly StreamWriter _log;
    public string Name { get; }
    public Process Process { get; }
    public bool ExpectedExit { get; set; }
    public Node(string name, string dll, string root, string log, string[] args)
    {
        Name = name; _log = new StreamWriter(log) { AutoFlush = true };
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process = new Process { StartInfo = start };
        Process.OutputDataReceived += (_, e) => Log(e.Data);
        Process.ErrorDataReceived += (_, e) => Log(e.Data);
        if (!Process.Start()) throw new InvalidOperationException("Cannot start " + name);
        Process.BeginOutputReadLine(); Process.BeginErrorReadLine();
    }
    private void Log(string? text) { if (text != null) lock (_log) _log.WriteLine(text); }
    public void Dispose()
    {
        if (!Process.HasExited) Process.Kill(entireProcessTree: true);
        Process.WaitForExit();
        Process.Dispose(); lock (_log) _log.Dispose();
    }
}

sealed record ProfileSnapshot(DateTimeOffset CapturedUtc, Dictionary<string, ProfileNodeSnapshot> Reports);
sealed record ProfileNodeSnapshot(DateTime ReportLastWriteUtc, string TransportName, JsonElement GcCounter, JsonElement AllocationPhases);
