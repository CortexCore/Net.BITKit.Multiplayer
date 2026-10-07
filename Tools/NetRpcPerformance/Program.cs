using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NetRpcPerformance;

try
{
    var options = Options.Parse(args);
    if (options.Role != "supervisor")
    {
        await using var node = new Node(options, options.Role);
        await node.Start(); await node.Loop();
    }
    else await Supervisor.Run(options);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

namespace NetRpcPerformance
{
    public sealed record Options
    {
        public string Role { get; init; } = "supervisor";
        public string Transport { get; init; } = "direct";
        public string Profile { get; init; } = "all";
        public int Port { get; init; }
        public int Iterations { get; init; } = 500;
        public int Warmup { get; init; } = 100;
        public int PayloadSize { get; init; } = 256;
        public int ContainerSize { get; init; } = 128;
        public string Sizes { get; init; } = "1,128,256";
        public string? SnapshotManifest { get; init; }
        public bool Trace { get; init; }
        public int Concurrency { get; init; } = 1;
        public int Clients { get; init; } = 1;
        public int ClientId { get; init; } = 1;
        public int Rate { get; init; }
        public int PacingMilliseconds { get; init; }
        public int IdleMilliseconds { get; init; } = 250;
        public int TimeoutSeconds { get; init; } = 30;
        public string Output { get; init; } = Path.GetFullPath(Path.Combine("Artifacts", "NetRpcPerformance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        public static readonly string[] Profiles = ["idle", "idle-sync", "control", "void", "scalar", "dto", "bytes", "component", "syncvar", "list", "dictionary"];
        public static bool IsRpc(string profile) => profile is "void" or "scalar" or "dto" or "bytes";
        public static bool HasNoBusinessOperations(string profile) => profile is "idle" or "idle-sync" or "control";
        public static string ClientRole(int clients, int id) => clients == 1 ? "client" : $"client-{id}";
        public static Options Parse(string[] args)
        {
            if (args.Length % 2 != 0) throw new ArgumentException("Options are --name value pairs.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2) if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option: " + args[i]);
            string Read(string name, string fallback) => values.Remove("--" + name, out var value) ? value : fallback;
            int Number(string name, int fallback) => int.Parse(Read(name, fallback.ToString()));
            var result = new Options
            {
                Role = Read("role", "supervisor"), Transport = Read("transport", "direct"), Profile = Read("profile", "all"),
                Port = Number("port", 0), Iterations = Number("iterations", 500), Warmup = Number("warmup", 100),
                PayloadSize = Number("payload-size", 256), ContainerSize = Number("container-size", 128), Sizes = Read("sizes", "1,128,256"),
                Concurrency = Number("concurrency", 1), PacingMilliseconds = Number("pacing-ms", 0), IdleMilliseconds = Number("idle-ms", 250),
                Clients = Number("clients", 1), ClientId = Number("client-id", 1), Rate = Number("rate", 0),
                Trace = Read("trace", "false") switch { "true" => true, "false" => false, _ => throw new ArgumentException("--trace must be true/false") },
                TimeoutSeconds = Number("timeout-seconds", 30), Output = Path.GetFullPath(Read("output", new Options().Output)),
                SnapshotManifest = Read("snapshot-manifest", "") is { Length: > 0 } manifest ? Path.GetFullPath(manifest) : null
            };
            if (values.Count != 0) throw new ArgumentException("Unknown option: " + values.Keys.First());
            if (result.Role is not ("supervisor" or "host" or "client" or "relay") || result.Transport is not ("direct" or "relay" or "both") ||
                result.Profile != "all" && !Profiles.Contains(result.Profile) || result.Iterations < 1 || result.Warmup < 1 ||
                result.PayloadSize < 1 || result.PayloadSize > 65536 || result.Concurrency < 1 || result.Concurrency > 256 ||
                result.Clients < 1 || result.Clients > 2 || result.ClientId < 1 || result.ClientId > result.Clients ||
                result.Clients > 1 && result.Transport != "direct" || result.Rate < 0 || result.Rate > 10000 ||
                result.Rate > 0 && (result.Concurrency != 1 || result.PacingMilliseconds != 0) ||
                result.ContainerSize < 1 || result.ContainerSize > 4096 || result.PacingMilliseconds < 0 || result.IdleMilliseconds < 1 || result.TimeoutSeconds < 1 ||
                result.Sizes.Split(',').Select(int.Parse).Any(n => n < 1 || n > 4096)) throw new ArgumentException("Invalid benchmark configuration.");
            return result;
        }
    }

    public sealed class Child : IAsyncDisposable
    {
        public Process Process { get; }
        private readonly Task<string> _errors;
        private readonly TimeSpan _timeout;
        public Child(Options options, string role, int port, int clientId = 1)
        {
            _timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            void Add(string key, object value) { start.ArgumentList.Add("--" + key); start.ArgumentList.Add(value.ToString()!); }
            Add("role", role); Add("port", port); Add("transport", options.Transport); Add("profile", options.Profile);
            Add("iterations", options.Iterations); Add("warmup", options.Warmup); Add("payload-size", options.PayloadSize);
            Add("container-size", options.ContainerSize); Add("concurrency", options.Concurrency); Add("pacing-ms", options.PacingMilliseconds);
            Add("idle-ms", options.IdleMilliseconds); Add("timeout-seconds", options.TimeoutSeconds);
            Add("clients", options.Clients); Add("client-id", clientId); Add("rate", options.Rate);
            Process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start child.");
            _errors = Process.StandardError.ReadToEndAsync();
        }
        public async Task<string> Read()
        {
            var line = await Process.StandardOutput.ReadLineAsync().WaitAsync(_timeout);
            if (line == null) throw new InvalidOperationException($"PID {Process.Id} exited: {await _errors}");
            return line;
        }
        public async Task<int> Ready()
        {
            var line = await Read(); if (!line.StartsWith("READY ")) throw new InvalidOperationException(line);
            return int.Parse(line[6..]);
        }
        public async Task<string> Command(string command)
        {
            await Process.StandardInput.WriteLineAsync(command); await Process.StandardInput.FlushAsync(); return await Read();
        }
        public async Task Ok(string command) { var reply = await Command(command); if (reply != "OK") throw new InvalidOperationException(reply); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    try
                    {
                        await Process.StandardInput.WriteLineAsync("quit").WaitAsync(TimeSpan.FromSeconds(2));
                        await Process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
                        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException)
                    {
                        if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
            }
            finally { Process.Dispose(); }
        }
    }

    public static class Supervisor
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public static async Task Run(Options options)
        {
            Directory.CreateDirectory(options.Output);
            if (options.SnapshotManifest != null)
            {
                using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(options.SnapshotManifest));
                var entries = snapshot.RootElement.GetProperty("Files"); int verified = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    string path = entry.GetProperty("Path").GetString()!;
                    string expected = entry.GetProperty("SHA256").GetString()!;
                    string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.GetFullPath(path))));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source snapshot changed: " + path);
                    verified++;
                }
                await File.WriteAllTextAsync(Path.Combine(options.Output, "source-proof.json"), JsonSerializer.Serialize(new { Manifest = options.SnapshotManifest, SourceRoot = Environment.CurrentDirectory, VerifiedOriginalFiles = verified, AllOriginalHashesMatch = true }, Json));
            }
            await File.WriteAllTextAsync(Path.Combine(options.Output, "build-proof.json"), JsonSerializer.Serialize(new
            {
                Assembly = Assembly.GetExecutingAssembly().Location,
                Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Assembly.GetExecutingAssembly().Location))),
                WovenActorReceivers = typeof(Actor).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("__netrpc_recv_")).Select(m => m.Name).ToArray(),
                WovenActorBodies = typeof(Actor).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.Name.StartsWith("__netrpc_body_")).Select(m => m.Name).ToArray(),
                NativeProxyReceivers = Assembly.GetExecutingAssembly().GetTypes().Where(t => t.Name.StartsWith("NetRemote_")).Select(t => new { Type = t.FullName, Receivers = t.GetMethods().Where(m => m.Name.StartsWith("__netrpc_recv_")).Select(m => m.Name).ToArray() }).ToArray(),
                CoreFramework = typeof(BITKit.Multiplayer.NetRpc.RpcContextService).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName,
                BuiltConfiguration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                ModelId = "openai/gpt-6.1-sol"
            }, Json));
            var summaries = new List<object>();
            foreach (var transport in options.Transport == "both" ? new[] { "direct", "relay" } : new[] { options.Transport })
            foreach (var profile in options.Profile == "all" ? Options.Profiles : new[] { options.Profile })
            foreach (int size in profile is "list" or "dictionary" ? options.Sizes.Split(',').Select(int.Parse) : new[] { options.ContainerSize })
            {
                var config = options with { Transport = transport, Profile = profile, ContainerSize = size };
                string name = $"{transport}-{profile}-{size}" + (config.Clients == 1 ? "" : $"-clients{config.Clients}");
                try
                {
                    var reports = await Case(config);
                    Validate(config, reports);
                    foreach (var report in reports) await File.WriteAllTextAsync(Path.Combine(options.Output, name + "-" + report.Role + ".json"), JsonSerializer.Serialize(new { Configuration = config, Report = report }, Json));
                    var host = reports.Single(r => r.Role == "host");
                    var clients = ClientReports(config, reports);
                    var summary = new
                    {
                        Case = name, Passed = true, Configuration = config, Reports = reports,
                        ProcessAllocationTotal = reports.Sum(r => r.AllocatedBytes),
                        BytesPerCompletedOperation = Options.HasNoBusinessOperations(profile) ? (double?)null : reports.Sum(r => r.AllocatedBytes) / (double)host.Completed,
                        AllocationDenominator = Options.HasNoBusinessOperations(profile) ? "none" : Options.IsRpc(profile) ? "actual Host RPC bodies across all clients" : "actual Host state mutations (each fans out to all clients)",
                        HostOperationsPerSecond = host.Completed / host.Seconds,
                        ClientCompletionsPerSecond = clients.Sum(c => c.Completed / c.Seconds),
                        PerClient = clients.Select(c => new { c.Role, c.Completed, c.Callbacks, c.ReturnFrames, c.ComponentFrames, c.FinalValue, c.FinalRevision, CompletionsPerSecond = c.Completed / c.Seconds }).ToArray(),
                        ComponentAppliedChangeRatio = profile == "component" && host.UdpSends > 0 ? clients.Sum(c => c.Callbacks) / (double?)host.UdpSends : null,
                        ComponentFrameDeliveryRatio = profile == "component" && host.UdpSends > 0 ? clients.Sum(c => c.ComponentFrames) / (double?)host.UdpSends : null,
                        UdpCountersAvailableAtHost = host.UdpSends.HasValue,
                        WorkloadScheduling = "RPC clients run sequentially within all process windows; each client performs N operations and fences before the next client starts. State uses one Host publisher with fanout to every client. Rate is per active worker, not per process measurement window.",
                        Notes = "Raw all-thread allocations. No idle/control subtraction. Native application-frame bytes exclude TCP/UDP/Relay envelopes. Host counters sum all attached counting decorators; received UDP frames/callbacks are independently observed per client. Relay endpoint process has allocation/timing only. Host sidecar receives its counting decorator during setup through public detach/attach after a read-only private peer lookup."
                    };
                    summaries.Add(summary);
                    await File.WriteAllTextAsync(Path.Combine(options.Output, name + "-aggregate.json"), JsonSerializer.Serialize(summary, Json));
                    Console.WriteLine($"PASS {name}: host={host.AllocatedBytes} B clients={clients.Sum(c => c.AllocatedBytes)} B total={reports.Sum(r => r.AllocatedBytes)} B max-client-p95={clients.Max(c => c.LatencyP95Ms):F3} ms callbacks={clients.Sum(c => c.Callbacks)}");
                }
                catch (Exception error)
                {
                    await File.WriteAllTextAsync(Path.Combine(options.Output, name + "-failure.json"), JsonSerializer.Serialize(new { Configuration = config, Error = error.ToString() }, Json)); throw;
                }
            }
            await File.WriteAllTextAsync(Path.Combine(options.Output, "suite.json"), JsonSerializer.Serialize(new { SupervisorPid = Environment.ProcessId, TimestampUtc = DateTime.UtcNow, Cases = summaries }, Json));
            Console.WriteLine("Evidence: " + options.Output);
        }
        private static async Task<NodeReport[]> Case(Options config)
        {
            await using var relay = config.Transport == "relay" ? new Child(config, "relay", 0) : null;
            int relayPort = relay == null ? 0 : await relay.Ready();
            await using var host = new Child(config, "host", relayPort); int directPort = await host.Ready();
            var clients = new List<Child>();
            var traces = new List<TraceCapture>();
            try
            {
                // Start every client before awaiting READY: the Host accepts all peers
                // before entering its control loop.
                for (int id = 1; id <= config.Clients; id++)
                    clients.Add(new Child(config, "client", config.Transport == "relay" ? relayPort : directPort, id));
                foreach (var client in clients) await client.Ready();
                await host.Ok("init"); foreach (var client in clients) await client.Ok("init");
                async Task Work(int count, int offset)
                {
                    if (Options.IsRpc(config.Profile))
                    {
                        for (int i = 0; i < clients.Count; i++)
                        {
                            // Actor.Fence returns a global Host count. Sequential clients
                            // make each fence exact, with no extra polling or void ACK.
                            int globalOffset = checked(offset * config.Clients + i * count);
                            await clients[i].Ok($"run {count} {globalOffset}");
                        }
                    }
                    else await host.Ok($"run {count} {offset}");
                    foreach (var client in clients) await client.Ok($"wait {count} {offset}");
                }
                await Work(config.Warmup, 0);
                await Task.Delay(100); // Setup, maps, initial snapshots and warmup are outside allocation windows.
                if (config.Trace)
                {
                    string prefix = $"{config.Transport}-{config.Profile}-{config.ContainerSize}" + (config.Clients == 1 ? "" : $"-clients{config.Clients}");
                    void Capture(Child child, string role) => traces.Add(new TraceCapture(child.Process.Id, Path.Combine(config.Output, $"{prefix}-{role}.nettrace")));
                    Capture(host, "host");
                    for (int i = 0; i < clients.Count; i++) Capture(clients[i], Options.ClientRole(config.Clients, i + 1));
                    if (relay != null) Capture(relay, "relay");
                    await Task.Delay(250); // Enable provider/markers before measurement.
                }
                await host.Ok("begin"); foreach (var client in clients) await client.Ok("begin");
                if (relay != null) await relay.Ok("begin");
                await Work(config.Iterations, config.Warmup);
                // Freeze ALL process windows before any lengthy JSON serialization/logging.
                foreach (var client in clients) await client.Ok("end");
                await host.Ok("end"); if (relay != null) await relay.Ok("end");
                var reports = new List<NodeReport> { JsonSerializer.Deserialize<NodeReport>(await host.Command("report"))! };
                foreach (var client in clients) reports.Add(JsonSerializer.Deserialize<NodeReport>(await client.Command("report"))!);
                if (relay != null) reports.Add(JsonSerializer.Deserialize<NodeReport>(await relay.Command("report"))!);
                return reports.ToArray();
            }
            finally
            {
                try { await Task.WhenAll(traces.Select(trace => trace.DisposeAsync().AsTask())); }
                finally { await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask())); }
            }
        }
        private static NodeReport[] ClientReports(Options config, NodeReport[] reports)
            => Enumerable.Range(1, config.Clients).Select(id => reports.Single(r => r.Role == Options.ClientRole(config.Clients, id))).ToArray();
        public static void Validate(Options config, NodeReport[] reports)
        {
            if (reports.Length != config.Clients + 1 + (config.Transport == "relay" ? 1 : 0) || reports.Select(r => r.Role).Distinct().Count() != reports.Length)
                throw new InvalidOperationException("Missing or duplicate process roles.");
            if (reports.Select(r => r.Pid).Distinct().Count() != reports.Length || reports.Any(r => r.Pid == Environment.ProcessId)) throw new InvalidOperationException("Processes are not independent.");
            if (reports.Any(r => r.AllocatedBytes < 0 || r.Seconds <= 0 || !r.Monotonic || !r.WovenReceiver)) throw new InvalidOperationException("Invalid report counters/proof.");
            var host = reports.Single(r => r.Role == "host"); var clients = ClientReports(config, reports);
            if (clients.Any(client => !client.ProxyType.Contains("NetRemote_", StringComparison.Ordinal))) throw new InvalidOperationException("Missing generated proxy proof.");
            if (Options.HasNoBusinessOperations(config.Profile))
            {
                if (reports.Any(r => r.Completed != 0 || r.Operations != 0)) throw new InvalidOperationException("Control/idle must not claim business operations.");
                if (config.Profile == "control" && reports.Any(r => r.SentBytes > 0 || r.ReceivedBytes > 0 || r.Callbacks != 0)) throw new InvalidOperationException("Pacing control unexpectedly performed network work.");
                return;
            }
            long expectedHost = (long)config.Iterations * (Options.IsRpc(config.Profile) ? config.Clients : 1);
            if (host.Completed != expectedHost) throw new InvalidOperationException($"Host completed {host.Completed}, expected {expectedHost}.");
            foreach (var client in clients)
            {
                if (config.Profile != "component" && client.Completed != config.Iterations) throw new InvalidOperationException($"{client.Role} completion count mismatch.");
                if (config.Profile is "component" or "syncvar" or "list" or "dictionary")
                {
                    if (client.FinalValue != config.Warmup + config.Iterations || host.FinalValue != client.FinalValue) throw new InvalidOperationException($"{client.Role} final state mismatch.");
                    if (config.Profile == "component" && (client.Completed != client.Callbacks || client.Callbacks < 1 || client.Callbacks > config.Iterations || client.FinalRevision != host.FinalRevision)) throw new InvalidOperationException($"{client.Role} invalid UDP delivery/revision count.");
                    if (config.Profile != "component" && client.Callbacks != config.Iterations) throw new InvalidOperationException($"{client.Role} reliable callbacks must be exact.");
                }
                if (config.Profile == "void" && (client.ReturnFrames != 1 || client.ReliableSends != config.Iterations + 1L)) throw new InvalidOperationException($"{client.Role} expected N void sends and one separate fence Return; void must not ACK.");
                if (config.Profile == "void" && client.OrdinaryBodyCalls != 0) throw new InvalidOperationException("Ordinary woven body must execute only at Host.");
                if (config.Profile is "scalar" or "dto" or "bytes" && (client.ReturnFrames != config.Iterations || client.ReliableSends != config.Iterations)) throw new InvalidOperationException($"{client.Role} warmed Task RPC requires exactly one reliable request/result per completed operation.");
                if (config.Profile == "component" && (client.ComponentFrames is null or < 1 || client.ComponentFrames > config.Iterations + 1L || client.Callbacks > client.ComponentFrames)) throw new InvalidOperationException($"{client.Role} UDP delivery counters are inconsistent; do not infer delivery from sends.");
            }
            if (config.Profile == "void" && (host.OrdinaryBodyCalls != (long)(config.Warmup + config.Iterations) * config.Clients || host.ReliableSends != config.Clients)) throw new InvalidOperationException("Host must execute every void body once and return only one fence per client.");
            if (config.Profile is "scalar" or "dto" or "bytes" && host.ReliableSends != expectedHost) throw new InvalidOperationException("Host result count mismatch.");
            if (config.Profile == "component" && host.UdpSends != (config.Iterations + 1L) * config.Clients) throw new InvalidOperationException("Host UDP submissions must include N mutations and one final repair per client.");
        }
    }
}
