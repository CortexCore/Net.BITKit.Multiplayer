using BITKit.Multiplayer.Samples.Arena;
using BITKit.Multiplayer.Samples.Arena.App;
using Xunit;

namespace Arena.AppTests;

public sealed class ArenaPresentationTests
{
    private static RoomSnapshot Frame(long tick, float x = 0, int health = 100, int deaths = 0,
        string peer = "peer", string scope = "room", bool player = true, bool bullet = true) => new()
    {
        Scope = scope, Tick = tick,
        Players = player ? [new PlayerPose { PlayerId = "p", PeerId = peer, X = x, AimX = x, Health = health, Deaths = deaths }] : [],
        Bullets = bullet ? [new BulletPose { Id = 1, BornTick = 1, X = x }] : []
    };

    [Fact]
    public void FractionalClockSmoothsAtSixtyHzWhileDirectUsesTheSameStaircaseSamples()
    {
        var smooth = new ArenaPresentation();
        var direct = new ArenaPresentation(enabled: false);
        var smoothX = new List<float>();
        var directX = new List<float>();
        int nextFrame = 30;
        for (int ms = 0; ms <= 1500; ms++)
        {
            long tick = ms / 50;
            RoomSnapshot source = Frame(tick, tick);
            RoomSnapshot a = smooth.Update(source, ms, true, false, "p");
            RoomSnapshot b = direct.Update(source, ms, true, false, "p");
            if (nextFrame <= 60 && ms == (int)Math.Round(nextFrame * 1000d / 60))
            {
                smoothX.Add(a.Players[0].X);
                directX.Add(b.Players[0].X);
                nextFrame++;
            }
        }
        float[] gradual = smoothX.Zip(smoothX.Skip(1), (a, b) => b - a).ToArray();
        float[] steps = directX.Zip(directX.Skip(1), (a, b) => b - a).ToArray();
        Assert.All(gradual, d => Assert.InRange(d, 0.31f, 0.35f));
        Assert.Contains(steps, d => d == 0);
        Assert.Contains(steps, d => d >= 1);
        Assert.True(smooth.InterpolatedFrames > 50);
        Assert.Equal(0, direct.InterpolatedFrames);
    }

    [Fact]
    public void DelayAndJitterAreBoundedAndLateDeliveryNeverRewindsClock()
    {
        var p = new ArenaPresentation(delayMilliseconds: 120, jitterMilliseconds: 80);
        double previous = double.NegativeInfinity;
        for (int ms = 0; ms < 3000; ms += 10)
        {
            RoomSnapshot frame = p.Update(Frame(ms / 50, ms / 50f), ms, true, false, "p");
            Assert.InRange(p.PendingSnapshots, 0, ArenaPresentation.Capacity);
            Assert.InRange(p.BufferedSnapshots, 0, ArenaPresentation.Capacity);
            if (p.BufferedSnapshots > 0 && previous != double.NegativeInfinity)
                Assert.True(p.RenderTick >= previous);
            Assert.True(float.IsFinite(frame.Players[0].X));
            if (p.BufferedSnapshots > 0) previous = p.RenderTick;
        }
        Assert.True(p.InterpolatedFrames > 0);
        Assert.True(p.PendingSnapshots > 0);
        float atEnd = p.Update(Frame(59, 59), 3000, true, false, "p").Players[0].X;
        for (int ms = 3010; ms < 6000; ms += 10)
            atEnd = p.Update(Frame(59, 59), ms, true, false, "p").Players[0].X;
        Assert.Equal(59f, atEnd);
        Assert.Equal(0, p.PendingSnapshots);
    }

    [Fact]
    public void ReorderedJitteredDeliveryDoesNotMoveDirectDisplayBackwards()
    {
        var p = new ArenaPresentation(delayMilliseconds: 100, jitterMilliseconds: 80, enabled: false);
        float previous = float.NegativeInfinity;
        for (int ms = 0; ms < 2000; ms += 5)
        {
            float current = p.Update(Frame(ms / 50, ms / 50f), ms, true, false, "p").Players[0].X;
            if (ms > 400) Assert.True(current >= previous);
            previous = current;
        }
        Assert.True(p.BufferedSnapshots > 0);
    }

    [Fact]
    public void ResetScopeDisconnectAndToggleDoNotReplayOldPoses()
    {
        var p = new ArenaPresentation();
        p.Update(Frame(1, 1), 50, true, false, "p");
        p.Update(Frame(2, 2), 100, true, false, "p");
        p.SetEnabled(false, 100);
        Assert.Equal(2, p.Update(Frame(2, 2), 110, true, false, "p").Players[0].X);
        p.SetEnabled(true, 110);
        Assert.True(p.RenderTick >= 0);
        Assert.Equal(90, p.Update(Frame(90, 90, scope: "new"), 120, true, false, "p").Players[0].X);
        Assert.Equal(1, p.BufferedSnapshots);
        p.Update(Frame(90), 130, false, false, "p");
        Assert.Equal(0, p.BufferedSnapshots);
        Assert.Equal(0, p.PendingSnapshots);
        Assert.True(p.ResetCount >= 2);
    }

    [Fact]
    public void LifecycleHealthAndMembershipAreImmediateAndInputIsNeverMutated()
    {
        var p = new ArenaPresentation(delayMilliseconds: 100);
        RoomSnapshot original = Frame(1, 1);
        RoomSnapshot first = p.Update(original, 50, true, false, "p");
        first.Players[0].X = 1000;
        first.Bullets[0].X = 1000;
        Assert.Equal(1, original.Players[0].X);
        Assert.Equal(1, original.Bullets[0].X);
        p.Update(Frame(2, 2), 100, true, false, "p");
        Assert.Equal(17, p.Update(Frame(2, 2, health: 17), 110, true, false, "p").Players[0].Health);
        Assert.Equal(20, p.Update(Frame(3, 20), 150, true, false, "p").Players[0].X); // teleport
        Assert.Empty(p.Update(Frame(3, player: false, bullet: false), 160, true, false, "p").Players);
        Assert.Empty(p.Update(Frame(3, player: false, bullet: false), 500, true, false, "p").Bullets);
        Assert.Equal(42, p.Update(Frame(4, 42, deaths: 1, peer: "other"), 550, true, false, "p").Players[0].X);
        Assert.Equal(50, p.Update(Frame(4, 50, deaths: 1, peer: "other"), 560, true, false, "p").Players[0].X); // same tick teleport
        Assert.True(p.ResetCount >= 2);
    }

    [Fact]
    public void RespawnAndHostOwnPoseBypassHistory()
    {
        var p = new ArenaPresentation();
        p.Update(Frame(1, 0, health: 0), 50, true, true, "p");
        RoomSnapshot host = p.Update(Frame(2, 2, health: 0), 100, true, true, "p");
        Assert.Equal(2, host.Players[0].X);
        RoomSnapshot alive = p.Update(Frame(2, 4, health: 100), 110, true, false, "p");
        Assert.Equal(4, alive.Players[0].X);
        Assert.Equal(100, alive.Players[0].Health);
        Assert.Equal(4, p.Update(Frame(3, 4, deaths: 1), 150, true, false, "p").Players[0].X);
    }

    [Fact]
    public void ReusedBulletIdWithNewLifetimeCannotReceiveOldScheduledPose()
    {
        var p = new ArenaPresentation(delayMilliseconds: 200);
        p.Update(Frame(1, 1), 50, true, false, "p");
        p.Update(Frame(2, 2), 100, true, false, "p");
        RoomSnapshot replacement = Frame(3, 9);
        replacement.Bullets[0].BornTick = 3;
        Assert.Equal(9, p.Update(replacement, 150, true, false, "p").Bullets[0].X);
        Assert.Equal(9, p.Update(replacement, 300, true, false, "p").Bullets[0].X);
        Assert.Equal(9, p.Update(replacement, 400, true, false, "p").Bullets[0].X);
    }

    [Fact]
    public void OlderViewCannotResurrectRemovedActorOrRestoreOldHealth()
    {
        var p = new ArenaPresentation();
        p.Update(Frame(1, 1), 50, true, false, "p");
        p.Update(Frame(2, 2, health: 7, bullet: false), 100, true, false, "p");
        Assert.Equal(7, p.Update(Frame(1, 1), 110, true, false, "p").Players[0].Health);
        p.Update(Frame(3, player: false, bullet: false), 150, true, false, "p");
        RoomSnapshot old = p.Update(Frame(2, 2), 160, true, false, "p");
        Assert.Empty(old.Players);
        Assert.Empty(old.Bullets);
    }

    [Fact]
    public void DisconnectedNonemptyAuthorityNeverAppearsInDisplay()
    {
        var p = new ArenaPresentation();
        p.Update(Frame(1, 1), 50, true, false, "p");
        RoomSnapshot stale = Frame(1, 1);
        RoomSnapshot display = p.Update(stale, 60, false, false, "p");
        Assert.Single(stale.Players);
        Assert.Single(stale.Bullets);
        Assert.Empty(display.Players);
        Assert.Empty(display.Bullets);
        Assert.Equal(0, p.BufferedSnapshots);
        Assert.Equal(0, p.PendingSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedLifecycleResetAnchorsPositionWithoutRewind(bool respawn)
    {
        var p = new ArenaPresentation(delayMilliseconds: 150);
        for (int tick = 1; tick <= 5; tick++)
            p.Update(Frame(tick, tick, health: respawn ? 0 : 100), tick * 50L, true, false, "p");
        RoomSnapshot reset = Frame(5, 30, deaths: respawn ? 1 : 0);
        Assert.Equal(30, p.Update(reset, 260, true, false, "p").Players[0].X);
        float prior = 30;
        for (int ms = 270; ms <= 850; ms += 10)
        {
            int tick = Math.Max(5, ms / 50);
            RoomSnapshot next = Frame(tick, 30 + tick - 5, deaths: respawn ? 1 : 0);
            RoomSnapshot frame = p.Update(next, ms, true, false, "p");
            Assert.True(frame.Players[0].X >= prior - 0.0001f, $"Reset rewound at {ms}ms: {prior} -> {frame.Players[0].X}");
            Assert.InRange(frame.Players[0].X, 30f, next.Players[0].X);
            prior = frame.Players[0].X;
        }
        Assert.True(prior > 30);
    }

    [Fact]
    public void InitialDelayedSpawnHoldsAnchorInsteadOfTrackingLiveThenRewinding()
    {
        var p = new ArenaPresentation(delayMilliseconds: 150);
        float previous = 5;
        for (int ms = 0; ms <= 700; ms += 10)
        {
            int tick = ms / 50;
            float display = p.Update(Frame(tick, 5 + tick), ms, true, false, "p").Players[0].X;
            if (ms < 150) Assert.Equal(5, display);
            Assert.True(display >= previous - 0.0001f);
            previous = display;
        }
        Assert.True(previous > 5);
    }

    [Fact]
    public void OpposingAimStaysFiniteAndUnitLengthDuringInterpolation()
    {
        var p = new ArenaPresentation(bufferMilliseconds: 50);
        RoomSnapshot a = Frame(1); a.Players[0].AimX = 1;
        p.Update(a, 50, true, false, "p");
        RoomSnapshot b = Frame(2); b.Players[0].AimX = -1;
        p.Update(b, 100, true, false, "p");
        RoomSnapshot middle = p.Update(b, 125, true, false, "p");
        PlayerPose aim = middle.Players[0];
        Assert.True(float.IsFinite(aim.AimX) && float.IsFinite(aim.AimZ));
        Assert.InRange(MathF.Sqrt(aim.AimX * aim.AimX + aim.AimZ * aim.AimZ), 0.999f, 1.001f);
    }

    [Fact]
    public void ReusedBulletWithDelayHoldsNewBornTickAnchor()
    {
        var p = new ArenaPresentation(delayMilliseconds: 150);
        for (int tick = 1; tick <= 5; tick++) p.Update(Frame(tick, tick), tick * 50L, true, false, "p");
        RoomSnapshot reset = Frame(5, 30); reset.Bullets[0].BornTick = 5;
        Assert.Equal(30, p.Update(reset, 260, true, false, "p").Bullets[0].X);
        float prior = 30;
        for (int ms = 270; ms <= 850; ms += 10)
        {
            int tick = Math.Max(5, ms / 50);
            RoomSnapshot next = Frame(tick, 30 + tick - 5); next.Bullets[0].BornTick = 5;
            float current = p.Update(next, ms, true, false, "p").Bullets[0].X;
            Assert.True(current >= prior - 0.0001f);
            prior = current;
        }
        Assert.True(prior > 30);
    }

    [Fact]
    public void BufferedClockRetainsFractionalMotionInLateSteadyDelayedInterval()
    {
        var smooth = new ArenaPresentation(delayMilliseconds: 150, jitterMilliseconds: 20);
        var direct = new ArenaPresentation(delayMilliseconds: 150, jitterMilliseconds: 20, enabled: false);
        var smoothFrames = new List<float>();
        var directFrames = new List<float>();
        int frameNumber = 60;
        for (int ms = 0; ms <= 1800; ms++)
        {
            RoomSnapshot source = Frame(ms / 50, ms / 50f);
            float a = smooth.Update(source, ms, true, false, "p").Players[0].X;
            float b = direct.Update(source, ms, true, false, "p").Players[0].X;
            if (frameNumber <= 90 && ms == (int)Math.Round(frameNumber * 1000d / 60))
            {
                smoothFrames.Add(a); directFrames.Add(b); frameNumber++;
            }
        }
        float[] fine = smoothFrames.Zip(smoothFrames.Skip(1), (a, b) => b - a).ToArray();
        float[] coarse = directFrames.Zip(directFrames.Skip(1), (a, b) => b - a).ToArray();
        Assert.True(fine.Count(d => d > 0.05f && d < 0.8f) >= 23, "Steady interval must have at least 23/30 fractional moves.");
        Assert.Contains(coarse, d => d >= 1f);
        Assert.True(smooth.InterpolatedFrames > 100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedDeliveryAfterNetworkSilenceOrRenderPauseRebasesLargeBacklog(bool pauseRendering)
    {
        var presentation = new ArenaPresentation(delayMilliseconds: 150);
        for (int ms = 0; ms <= 500; ms += 10)
            presentation.Update(Frame(ms / 50), ms, true, false, "p");
        if (!pauseRendering)
            for (int ms = 510; ms < 2000; ms += 10)
                presentation.Update(Frame(10), ms, true, false, "p");
        var previousTick = presentation.RenderTick;
        RoomSnapshot? rendered = null;
        for (int ms = 2000; ms <= 2350; ms += 10)
        {
            rendered = presentation.Update(Frame(100 + (ms - 2000) / 50, x: 1), ms, true, false, "p");
            Assert.True(presentation.RenderTick >= previousTick);
            previousTick = presentation.RenderTick;
        }
        Assert.True(presentation.RenderTick >= 98, "Recovery must not spend seconds replaying obsolete history.");
        Assert.InRange(rendered!.Players[0].X, .95f, 1f);
        Assert.InRange(rendered.Bullets[0].X, .95f, 1f);
    }
}
