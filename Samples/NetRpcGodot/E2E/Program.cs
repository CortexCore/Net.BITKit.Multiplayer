using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BITKit.Multiplayer.Samples.NetRpcGodot;

string Argument(string name, string fallback) { int i = Array.IndexOf(args, name); return i < 0 ? fallback : args[i + 1]; }
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
string godot = Argument("--godot", Environment.GetEnvironmentVariable("GODOT_BIN") ?? (OperatingSystem.IsWindows() ? "godot" : "godot4"));
bool useRelay = args.Contains("--relay"), visible = args.Contains("--visible"), liteNetLib = args.Contains("--litenetlib");
if (liteNetLib && useRelay) throw new ArgumentException("LiteNetLib Relay is not implemented; DIRECT only.");
string backend = liteNetLib ? "LiteNetLib DIRECT" : useRelay ? "TCP+UDP Relay" : "TCP+UDP Direct";
string output = Path.Combine(root, "Artifacts/NetRpcGodot", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + (liteNetLib ? "litenetlib-direct" : useRelay ? "relay" : "direct")); Directory.CreateDirectory(output);
int FreePort() { var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port; }
int directPort = FreePort(), relayPort = useRelay ? FreePort() : 0;
var owned = new List<(Process Process, ConcurrentQueue<string> Log)>();
async Task<Process> Start(string executable, IEnumerable<string> arguments, string label, bool ready = false)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    var process = new Process { StartInfo = start, EnableRaisingEvents = true }; var log = new ConcurrentQueue<string>();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Record(string? line) { if (line == null) return; log.Enqueue(line); if (line.StartsWith("READY")) started.TrySetResult(); }
    process.OutputDataReceived += (_, e) => Record(e.Data); process.ErrorDataReceived += (_, e) => Record(e.Data);
    process.Start(); owned.Add((process, log)); process.BeginOutputReadLine(); process.BeginErrorReadLine();
    if (ready) await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"START {label} pid={process.Id}"); return process;
}
try
{
    if (Path.IsPathFullyQualified(godot) && !File.Exists(godot)) throw new FileNotFoundException("Godot .NET executable not found.", godot);
    if (useRelay) await Start("dotnet", new[] { Path.Combine(root, "Artifacts/bin/NetRpcGodot.Relay/Release/net10.0/NetRpcGodot.Relay.dll"), "--port", relayPort.ToString(), "--seconds", "60" }, "Relay", true);
    var hostArgs = new List<string> { Path.Combine(root, "Artifacts/bin/NetRpcGodot.Host/Release/net10.0/NetRpcGodot.Host.dll"), "--port", directPort.ToString(), "--seconds", "60" };
    if (useRelay) { hostArgs.Add("--relay-port"); hostArgs.Add(relayPort.ToString()); }
    if (liteNetLib) hostArgs.Add("--litenetlib");
    await Start("dotnet", hostArgs, "Host", true); if (useRelay) await Task.Delay(500);
    var clients = new List<Process>();
    for (int slot = 1; slot <= 2; slot++)
    {
        var launch = new List<string>(); if (!visible) launch.Add("--headless");
        launch.AddRange(new[] { "--path", Path.Combine(root, "Samples/NetRpcGodot/Godot") });
        if (visible) launch.AddRange(new[] { "--position", slot == 1 ? "20,70" : "650,160" });
        launch.AddRange(new[] { "--", "--auto", "--slot", slot.ToString(), "--port", (useRelay ? relayPort : directPort).ToString(), "--result", Path.Combine(output, "client" + slot + ".json") });
        if (liteNetLib) launch.Add("--litenetlib");
        if (visible) launch.AddRange(new[] { "--capture", Path.Combine(output, "client" + slot + ".png") });
        clients.Add(await Start(godot, launch, "Godot Client " + slot));
    }
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
    await Task.WhenAll(clients.Select(p => p.WaitForExitAsync(deadline.Token)));
    var results = Enumerable.Range(1, 2).Select(i => JsonSerializer.Deserialize<ClientProof>(File.ReadAllText(Path.Combine(output, "client" + i + ".json")))!).ToArray();
    if (clients.Any(p => p.ExitCode != 0) || results.Any(r => !r.Passed || !r.RealGodot)) throw new Exception("Godot assertions failed:\n" + string.Join("\n", results.Select(r => r.Error)));
    var summary = new { Passed = true, Mode = useRelay ? "Relay" : "Direct", Backend = backend, Godot = godot, IndependentClients = clients.Select(p => p.Id).ToArray(), Output = output, Results = results };
    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS {backend}: TWO REAL GODOT PROCESSES; Host authority, movement, woven attack, list/dictionary, loss/reorder recovery, disconnect/reconnect. Evidence={output}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Evidence=" + output); return 1; }
finally
{
    foreach (var entry in owned)
    {
        if (!entry.Process.HasExited) entry.Process.Kill(entireProcessTree: true);
        await entry.Process.WaitForExitAsync();
        File.WriteAllLines(Path.Combine(output, "process-" + entry.Process.Id + ".log"), entry.Log);
        entry.Process.Dispose();
    }
}
