using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

if (args is ["--worker"])
{
    Console.WriteLine("ready");
    Console.ReadLine();
    SmokeWork.StartupNoise();
    BenchmarkEvents.Log.MeasurementStart("smoke", "allocation-and-cpu", 10000);
    SmokeWork.MeasuredAllocation();
    SmokeWork.MeasuredCpu();
    BenchmarkEvents.Log.MeasurementStop();
    SmokeWork.ReportNoise();
    Console.WriteLine("done");
    Console.ReadLine(); // Keep runtime alive until collector requests rundown.
    return;
}
if (args.Length != 1) throw new ArgumentException("Usage: dotnet run --project Tools/NetRpcTrace/Smoke -- <existing-output-directory>");
var directory = Path.GetFullPath(args[0]);
if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
var tracePath = Path.Combine(directory, "smoke.nettrace");
var reportPath = Path.Combine(directory, "smoke-report.json");
var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
start.ArgumentList.Add(typeof(SmokeWork).Assembly.Location); start.ArgumentList.Add("--worker");
using var worker = Process.Start(start)!;
try
{
    if (await worker.StandardOutput.ReadLineAsync() != "ready") throw new Exception("Worker failed startup");
    var client = new DiagnosticsClient(worker.Id);
    // GC + type names + JIT/loader + stack keyword; normal end-of-session rundown enabled.
    using var session = client.StartEventPipeSession([
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x41000019),
        new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
        new EventPipeProvider("BITKit-NetRpc-Benchmark", EventLevel.Informational)], requestRundown: true);
    await using var trace = File.Create(tracePath);
    var copy = session.EventStream.CopyToAsync(trace);
    await worker.StandardInput.WriteLineAsync("go"); await worker.StandardInput.FlushAsync();
    if (await worker.StandardOutput.ReadLineAsync() != "done") throw new Exception("Worker failed workload");
    session.Stop(); await copy; await trace.FlushAsync(); trace.Close();
    await worker.StandardInput.WriteLineAsync("exit"); await worker.StandardInput.FlushAsync();
    await worker.WaitForExitAsync();
    var analyzer = Path.Combine(Path.GetDirectoryName(typeof(SmokeWork).Assembly.Location)!, "NetRpcTrace.dll");
    var analyzeStart = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    foreach (var arg in new[] { analyzer, "--input", tracePath, "--output", reportPath, "--preview" }) analyzeStart.ArgumentList.Add(arg);
    using var analysis = Process.Start(analyzeStart)!; await analysis.WaitForExitAsync();
    if (analysis.ExitCode != 0) throw new Exception("Analyzer rejected smoke capture");
    using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
    var root = document.RootElement; var windows = root.GetProperty("windows");
    if (windows.GetArrayLength() != 1 || root.GetProperty("markers").GetArrayLength() != 2) throw new Exception("Expected one paired window");
    var window = windows[0];
    if (window.GetProperty("processId").GetInt32() != worker.Id || window.GetProperty("wholeTrace").GetBoolean()) throw new Exception("PID/marker filtering failed");
    var allocations = window.GetProperty("allocations"); var cpu = window.GetProperty("cpu");
    if (allocations.GetProperty("validAllocationSamples").GetInt64() <= 0 || allocations.GetProperty("weightedBytes").GetInt64() <= 0 || cpu.GetProperty("sampleCount").GetInt64() <= 0) throw new Exception("No real allocation/CPU samples");
    var allocationNames = allocations.GetProperty("inclusiveFrames").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
    var cpuNames = cpu.GetProperty("inclusiveFrames").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
    if (!allocationNames.Any(n => n!.Contains("MeasuredAllocation")) || !cpuNames.Any(n => n!.Contains("MeasuredCpu"))) throw new Exception("Managed allocating/CPU symbol resolution failed");
    if (allocationNames.Concat(cpuNames).Any(n => n!.Contains("StartupNoise") || n.Contains("ReportNoise"))) throw new Exception("Startup/report samples contaminated measured window");
    var weights = allocations.GetProperty("types").EnumerateArray().Sum(t => t.GetProperty("weight").GetInt64());
    if (weights != allocations.GetProperty("weightedBytes").GetInt64()) throw new Exception("Type weight conservation failed");
    Console.WriteLine("PASS: real EventPipe capture, dynamic markers, PID/window exclusion, typed allocations, JIT/rundown allocation and CPU stacks, type weight conservation.");
}
finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }

[EventSource(Name = "BITKit-NetRpc-Benchmark")]
sealed class BenchmarkEvents : EventSource
{
    public static readonly BenchmarkEvents Log = new();
    [Event(1, Level = EventLevel.Informational)] public void MeasurementStart(string role, string profile, int operations) => WriteEvent(1, role, profile, operations);
    [Event(2, Level = EventLevel.Informational)] public void MeasurementStop() => WriteEvent(2);
}
static class SmokeWork
{
    private static object? sink;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void StartupNoise() { for (var i = 0; i < 1000; i++) sink = new byte[8192]; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ReportNoise() { for (var i = 0; i < 1000; i++) sink = new byte[8192]; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void MeasuredAllocation() { for (var i = 0; i < 10000; i++) sink = new byte[8192]; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void MeasuredCpu()
    {
        var timer = Stopwatch.StartNew(); ulong value = 17;
        while (timer.ElapsedMilliseconds < 1000) for (var i = 0; i < 10000; i++) value = unchecked(value * 6364136223846793005UL + 1);
        sink = value;
    }
}
