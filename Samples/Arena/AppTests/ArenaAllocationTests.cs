using System.Text.Json;
using BITKit.Multiplayer.Samples.Arena;
using Xunit;

namespace Arena.AppTests;

public sealed class ArenaAllocationTests
{
    [Fact]
    public void DisabledAndWarmupIntervalsDoNotCount()
    {
        var report = new ArenaReport();
        Assert.Null(report.Allocations);
        var phases = new ArenaAllocationReport();
        phases.Start(ArenaAllocationPhase.ViewClone).End();
        Assert.Equal(0, phases.ViewClone.Count);
        Assert.Equal(0, phases.ViewClone.Bytes);
        Assert.False(phases.Sampling);
    }

    [Fact]
    public void EnabledIntervalsOnlyCreditTheirOwnLeaf()
    {
        var phases = new ArenaAllocationReport();
        phases.EnableAfterWarmup();
        var view = phases.Start(ArenaAllocationPhase.ViewClone);
        byte[] allocated = new byte[1024];
        GC.KeepAlive(allocated);
        view.End();
        phases.Start(ArenaAllocationPhase.InputSubmit).End();
        Assert.Equal(1, phases.ViewClone.Count);
        Assert.True(phases.ViewClone.Bytes >= 1024);
        Assert.Equal(1, phases.InputSubmit.Count);
        Assert.Equal(0, phases.MotionSend.Count);
        Assert.True(phases.ViewClone.ElapsedTicks >= 0);
        string json = JsonSerializer.Serialize(new ArenaReport { Allocations = phases });
        Assert.Contains("\"ViewClone\"", json);
        Assert.Contains("\"StopwatchFrequency\"", json);
    }
}
