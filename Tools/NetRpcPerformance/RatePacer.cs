using System.Diagnostics;

namespace NetRpcPerformance;

/// <summary>Harness-only absolute-deadline pacing. No per-operation timer/task allocation.
/// Sleep granularity can miss a deadline; measured throughput remains authoritative.</summary>
public sealed class RatePacer
{
    private readonly int _rate;
    private long _start, _count;
    public RatePacer(int rate)
    {
        if (rate < 0 || rate > 10000) throw new ArgumentOutOfRangeException(nameof(rate));
        _rate = rate;
    }
    public void Reset() { _start = Stopwatch.GetTimestamp(); _count = 0; }
    public void Wait()
    {
        if (_rate == 0) return;
        long target = _start + (++_count * Stopwatch.Frequency / _rate);
        while (true)
        {
            long remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            Thread.Sleep((int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 1000));
        }
    }
}
