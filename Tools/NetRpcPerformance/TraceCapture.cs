using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace NetRpcPerformance;

[EventSource(Name = "BITKit-NetRpc-Benchmark")]
public sealed class BenchmarkEvents : EventSource
{
    public static readonly BenchmarkEvents Log = new();
    [Event(1, Level = EventLevel.Informational)] public void MeasurementStart(string role, string profile, int operations) { if (IsEnabled()) WriteEvent(1, role, profile, operations); }
    [Event(2, Level = EventLevel.Informational)] public void MeasurementStop() { if (IsEnabled()) WriteEvent(2); }
}

public sealed class TraceCapture : IAsyncDisposable
{
    private readonly EventPipeSession _session;
    private readonly FileStream _file;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _copy;
    public TraceCapture(int pid, string path)
    {
        _file = File.Create(path);
        try
        {
            _session = new DiagnosticsClient(pid).StartEventPipeSession(new[] {
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x41000019),
                new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                new EventPipeProvider("BITKit-NetRpc-Benchmark", EventLevel.Informational)
            }, requestRundown: true, circularBufferMB: 64);
            _copy = _session.EventStream.CopyToAsync(_file, _cancel.Token);
        }
        catch { _file.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(_session.Stop).WaitAsync(TimeSpan.FromSeconds(10)); await _copy.WaitAsync(TimeSpan.FromSeconds(10)); await _file.FlushAsync(); }
        finally { _cancel.Cancel(); _session.Dispose(); await _file.DisposeAsync(); _cancel.Dispose(); }
    }
}
