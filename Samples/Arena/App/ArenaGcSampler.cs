using System.Diagnostics;
using BITKit.Multiplayer.Samples.Arena;

namespace BITKit.Multiplayer.Samples.Arena.App;

// Process-wide diagnostics, deliberately sampled at most once per second and stored
// as three snapshots. Never forces a collection or retains sampled gameplay objects.
internal sealed class ArenaGcSampler
{
    private long readySince = -1;
    private ArenaGcSample? baseline;
    private ArenaGcSample? previous;

    internal void Sample(ArenaGcReport report, long elapsedMilliseconds, bool ready, bool final = false)
    {
        if (!ready) return;
        if (readySince < 0) { readySince = elapsedMilliseconds; return; }
        if (elapsedMilliseconds - readySince < report.WarmupSeconds * 1000L) return;
        if (previous != null && elapsedMilliseconds - previous.ElapsedMilliseconds < (final ? 1 : 1000)) return;

        var current = Capture(elapsedMilliseconds);
        if (baseline == null)
        {
            baseline = previous = current;
            report.Initial = report.PeakManagedHeap = report.Final = current;
            return;
        }

        var intervalMs = current.ElapsedMilliseconds - previous!.ElapsedMilliseconds;
        var allocated = Math.Max(0, current.AllocatedBytes - baseline.AllocatedBytes);
        report.SampleCount++;
        report.WindowMilliseconds = current.ElapsedMilliseconds - baseline.ElapsedMilliseconds;
        report.TotalAllocatedBytes = allocated;
        report.AllocatedBytesPerSecond = allocated * 1000d / report.WindowMilliseconds;
        if (intervalMs >= 750)
            report.PeakSampleAllocatedBytesPerSecond = Math.Max(report.PeakSampleAllocatedBytesPerSecond,
                Math.Max(0, current.AllocatedBytes - previous.AllocatedBytes) * 1000d / intervalMs);
        report.Gen0Collections = Math.Max(0, current.Gen0Collections - baseline.Gen0Collections);
        report.Gen1Collections = Math.Max(0, current.Gen1Collections - baseline.Gen1Collections);
        report.Gen2Collections = Math.Max(0, current.Gen2Collections - baseline.Gen2Collections);
        if (current.ManagedHeapBytes > report.PeakManagedHeap!.ManagedHeapBytes) report.PeakManagedHeap = current;
        report.Final = previous = current;
    }

    private static ArenaGcSample Capture(long elapsedMilliseconds)
    {
        // Precise process-wide allocation counter; no thread-local send-only inference.
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var info = GC.GetGCMemoryInfo();
        long heap = GC.GetTotalMemory(forceFullCollection: false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ArenaGcSample
        {
            ElapsedMilliseconds = elapsedMilliseconds, AllocatedBytes = allocated,
            Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2),
            ManagedHeapBytes = heap, HeapCommittedBytes = info.TotalCommittedBytes,
            FragmentedBytes = info.FragmentedBytes, WorkingSetBytes = process.WorkingSet64
        };
    }
}
