using System.Diagnostics;
using System.Numerics;
using BITKit.Multiplayer.Samples.Arena;
using Raylib_cs;

namespace BITKit.Multiplayer.Samples.Arena.App;

internal sealed class ArenaController
{
    private readonly IArenaSession session;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ArenaPresentation presentation;
    private readonly ArenaGcSampler gcSampler = new();
    private readonly ArenaAllocationReport? phases;
    private long readySince = -1;
    private long lastReport;
    private long lastMove;
    private long lastFire;
    private bool udpOffApplied, udpOnApplied, udpRebindApplied;
    private uint moveSequence;
    private uint fireSequence;

    internal ArenaController(IArenaSession session, AppOptions options, ArenaReport report)
    {
        this.session = session;
        Options = options;
        Report = report;
        phases = report.Allocations;
        if (phases != null) session.SetAllocationPhases(phases);
        // Inject display-pose impairment on Client only; Host displays receipt without artificial delay.
        presentation = new ArenaPresentation(options.InterpolationMilliseconds,
            session.IsHost ? 0 : options.PoseDelayMilliseconds,
            session.IsHost ? 0 : options.PoseJitterMilliseconds, options.InterpolationEnabled);
    }

    internal AppOptions Options { get; }
    internal ArenaReport Report { get; }
    internal RoomSnapshot View { get; private set; } = new();
    internal RoomSnapshot RenderView { get; private set; } = new();
    internal void ToggleInterpolation() => presentation.SetEnabled(!presentation.Enabled, clock.ElapsedMilliseconds);
    internal void ToggleUdp() => session.SetUdpEnabled(!session.GetDatagramReport().Enabled);
    internal void RebindUdp() => _ = RebindUdpAsync(automated: false);
    private async Task RebindUdpAsync(bool automated)
    {
        if (session.IsHost) return;
        try
        {
            await session.RebindUdpAsync();
            Console.WriteLine("UDP_REBIND_OK"); // E2E observes completion, not merely the scheduled request.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UDP_REBIND_FAILED ({ex.GetType().Name})");
            if (automated) Report.Error = "Automated UDP rebind failed (" + ex.GetType().Name + ")";
        }
    }
    internal bool Expired => Options.Duration > 0 && clock.Elapsed.TotalSeconds >= Options.Duration;
    internal bool Ready => session.IsReady;
    internal bool IsHost => session.IsHost;
    internal string RoomName => session.RoomName;
    internal string PlayerId => session.LocalPlayerId;
    internal string PeerId => session.LocalPeerId;
    internal string RoomId => session.RoomId;
    internal ArenaConnectionMode ConnectionMode => session.ConnectionMode;
    internal string ConnectedEndpoint => session.ConnectedEndpoint;

    internal void Update(bool graphical, ArenaRenderer? renderer = null)
    {
        if (!session.IsReady && clock.Elapsed.TotalSeconds >= 15)
            throw new InvalidOperationException("Arena did not become ready within 15 seconds.");
        long now = clock.ElapsedMilliseconds;
        if (phases != null && session.IsReady)
        {
            if (readySince < 0) readySince = now;
            if (now - readySince >= phases.WarmupSeconds * 1000L) phases.EnableAfterWarmup();
        }
        var interval = phases?.Start(ArenaAllocationPhase.ViewClone) ?? default;
        View = session.GetView();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.AppOther) ?? default;
        ObserveView();
        if (!udpOffApplied && Options.UdpOffAfter.HasValue && clock.Elapsed.TotalSeconds >= Options.UdpOffAfter.Value)
        { session.SetUdpEnabled(false); udpOffApplied = true; }
        if (!udpOnApplied && Options.UdpOnAfter.HasValue && clock.Elapsed.TotalSeconds >= Options.UdpOnAfter.Value)
        { session.SetUdpEnabled(true); udpOnApplied = true; }
        if (!udpRebindApplied && Options.UdpRebindAfter.HasValue && clock.Elapsed.TotalSeconds >= Options.UdpRebindAfter.Value)
        { udpRebindApplied = true; _ = RebindUdpAsync(automated: true); }
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.Datagrams) ?? default;
        Report.Datagrams = session.GetDatagramReport();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.Interpolation) ?? default;
        RenderView = presentation.Update(View, now, session.IsReady, session.IsHost, session.LocalPlayerId);
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.ReportMetadata) ?? default;
        UpdatePresentationReport();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.GcSample) ?? default;
        gcSampler.Sample(Report.Gc, now, session.IsReady);
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.AppOther) ?? default;
        if (session.IsReady && !Report.WasReady)
        {
            Report.WasReady = true;
            Console.WriteLine($"READY {Options.Role} room={session.RoomId} player={session.LocalPlayerId} peer={session.LocalPeerId} route={session.ConnectionMode} endpoint={session.ConnectedEndpoint}");
            if (Options.ReadyFile.Length != 0)
                Program.WriteJsonAtomically(Options.ReadyFile, new
                {
                    Role = Options.Role,
                    RoomId = session.RoomId,
                    RoomName = session.RoomName,
                    PlayerId = session.LocalPlayerId,
                    PeerId = session.LocalPeerId,
                    IsReady = true,
                    ConnectionMode = session.ConnectionMode,
                    ConnectedEndpoint = session.ConnectedEndpoint
                });
        }
        interval.End();

        interval = phases?.Start(ArenaAllocationPhase.InputSubmit) ?? default;
        if (session.IsReady && PlayerId.Length != 0)
        {
            PlayerPose? own = View.Players.FirstOrDefault(p => p.PlayerId == PlayerId);
            if (own is { Health: > 0 })
            {
                if (now - lastMove >= 50)
                {
                    (float x, float z) = Move(graphical);
                    session.SubmitMove(x, z, ++moveSequence);
                    lastMove = now;
                }
                if (now - lastFire >= 270 && ShouldFire(graphical, renderer))
                {
                    Vector2 aim = Aim(own, graphical, renderer);
                    if (aim.LengthSquared() > 0.0001f)
                    {
                        aim = Vector2.Normalize(aim);
                        session.Fire(aim.X, aim.Y, ++fireSequence);
                        lastFire = now;
                    }
                }
            }
        }
        interval.End();
        if (now - lastReport >= 250)
        {
            interval = phases?.Start(ArenaAllocationPhase.ReportWrite) ?? default;
            if (Options.Report.Length != 0) Program.WriteJsonAtomically(Options.Report, Report);
            interval.End();
            lastReport = now;
        }
    }

    private void ObserveView()
    {
        Report.IsReady = session.IsReady;
        Report.Final = View;
        Report.ViewsObserved++;
        Report.MaximumPlayers = Math.Max(Report.MaximumPlayers, View.Players.Length);
        PlayerPose? own = View.Players.FirstOrDefault(p => p.PlayerId == PlayerId);
        if (own != null) Report.MinimumOwnHealth = Math.Min(Report.MinimumOwnHealth, own.Health);
        if (session.LastError.Length != 0) Report.Error = Program.SafeError(session.LastError, Options);
    }

    private void UpdatePresentationReport()
    {
        ArenaPresentationReport diagnostics = Report.Presentation;
        diagnostics.Enabled = presentation.Enabled;
        diagnostics.BufferMilliseconds = presentation.BufferMilliseconds;
        diagnostics.SimulatedDelayMilliseconds = presentation.DelayMilliseconds;
        diagnostics.SimulatedJitterMilliseconds = presentation.JitterMilliseconds;
        diagnostics.RenderTick = presentation.RenderTick;
        diagnostics.BufferedSnapshots = presentation.BufferedSnapshots;
        diagnostics.PendingSnapshots = presentation.PendingSnapshots;
        diagnostics.InterpolatedFrames = presentation.InterpolatedFrames;
        diagnostics.ResetCount = presentation.ResetCount;
        diagnostics.Frame = RenderView;
    }

    private (float, float) Move(bool graphical)
    {
        if (Options.Bot == "move")
        {
            double angle = clock.Elapsed.TotalSeconds * 0.65 + (IsHost ? 0 : Math.PI);
            return ((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
        if (graphical && Options.Bot == "idle")
        {
            float x = (Raylib.IsKeyDown(KeyboardKey.D) ? 1 : 0) - (Raylib.IsKeyDown(KeyboardKey.A) ? 1 : 0);
            float z = (Raylib.IsKeyDown(KeyboardKey.S) ? 1 : 0) - (Raylib.IsKeyDown(KeyboardKey.W) ? 1 : 0);
            float length = MathF.Sqrt(x * x + z * z);
            return length > 1 ? (x / length, z / length) : (x, z);
        }
        return (0, 0);
    }

    private bool ShouldFire(bool graphical, ArenaRenderer? renderer) => Options.Bot == "shoot" ||
        (graphical && Options.Bot == "idle" && renderer?.MouseInsideArena() == true &&
         Raylib.IsMouseButtonDown(MouseButton.Left));

    private Vector2 Aim(PlayerPose own, bool graphical, ArenaRenderer? renderer)
    {
        if (Options.Bot == "shoot")
        {
            PlayerPose? target = View.Players
                .Where(p => p.PlayerId != PlayerId && p.Health > 0)
                .OrderBy(p => (p.X - own.X) * (p.X - own.X) + (p.Z - own.Z) * (p.Z - own.Z))
                .FirstOrDefault();
            return target == null ? Vector2.Zero : new Vector2(target.X - own.X, target.Z - own.Z);
        }
        return graphical && renderer != null
            ? renderer.MouseWorld() - new Vector2(own.X, own.Z)
            : Vector2.Zero;
    }

    internal void Finish()
    {
        var interval = phases?.Start(ArenaAllocationPhase.ViewClone) ?? default;
        View = session.GetView();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.Datagrams) ?? default;
        Report.Datagrams = session.GetDatagramReport();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.AppOther) ?? default;
        ObserveView();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.Interpolation) ?? default;
        RenderView = presentation.Update(View, clock.ElapsedMilliseconds, session.IsReady, session.IsHost, session.LocalPlayerId);
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.ReportMetadata) ?? default;
        UpdatePresentationReport();
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.GcSample) ?? default;
        gcSampler.Sample(Report.Gc, clock.ElapsedMilliseconds, session.IsReady, final: true);
        interval.End();
        interval = phases?.Start(ArenaAllocationPhase.ReportWrite) ?? default;
        if (Options.Report.Length != 0) Program.WriteJsonAtomically(Options.Report, Report);
        interval.End();
    }
}
