using BITKit.Multiplayer.Samples.Arena;

namespace BITKit.Multiplayer.Samples.Arena.App;

// Local display-only sampling. All times are supplied by the caller (monotonic milliseconds).
internal sealed class ArenaPresentation
{
    internal const int Capacity = 32;
    private const double TicksPerMillisecond = 0.02; // 20 Hz
    private const float TeleportDistanceSquared = 36;
    private readonly List<Sample> history = new();
    private readonly List<Delivery> pending = new();
    private readonly Dictionary<string, PlayerPose> players = new();
    private readonly Dictionary<long, BulletPose> bullets = new();
    private readonly Dictionary<string, Anchor<PlayerPose>> playerAnchors = new();
    private readonly Dictionary<long, Anchor<BulletPose>> bulletAnchors = new();
    private uint random = 0xA341316Cu;
    private long observedTick = long.MinValue;
    private long deliveredTick = long.MinValue;
    private long lastTime;
    private double renderTick;
    private bool clockStarted;
    private string scope = "";
    private bool connected;
    private RoomSnapshot? latestAuthority;

    internal ArenaPresentation(int bufferMilliseconds = 100, int delayMilliseconds = 0, int jitterMilliseconds = 0,
        bool enabled = true)
    {
        if (bufferMilliseconds is < 0 or > 500 || delayMilliseconds is < 0 or > 1000 || jitterMilliseconds is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        BufferMilliseconds = bufferMilliseconds;
        DelayMilliseconds = delayMilliseconds;
        JitterMilliseconds = jitterMilliseconds;
        Enabled = enabled;
    }

    internal int BufferMilliseconds { get; }
    internal int DelayMilliseconds { get; }
    internal int JitterMilliseconds { get; }
    internal bool Enabled { get; private set; }
    internal double RenderTick => Enabled ? renderTick : deliveredTick == long.MinValue ? 0 : deliveredTick;
    internal int BufferedSnapshots => history.Count;
    internal int PendingSnapshots => pending.Count;
    internal long InterpolatedFrames { get; private set; }
    internal long ResetCount { get; private set; }

    internal void SetEnabled(bool enabled, long now)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        // Re-enable at most one configured buffer behind the live display, not at an old startup clock.
        if (enabled && deliveredTick != long.MinValue)
            renderTick = Math.Max(renderTick - BufferMilliseconds * TicksPerMillisecond,
                deliveredTick - BufferMilliseconds * TicksPerMillisecond);
        lastTime = now;
    }

    internal void Clear()
    {
        history.Clear(); pending.Clear(); players.Clear(); bullets.Clear(); playerAnchors.Clear(); bulletAnchors.Clear();
        observedTick = deliveredTick = long.MinValue;
        renderTick = 0;
        clockStarted = false;
        scope = "";
        connected = false;
        latestAuthority = null;
        ResetCount++;
    }

    internal RoomSnapshot Update(RoomSnapshot authority, long now, bool ready, bool isHost, string localPlayerId)
    {
        if (now < lastTime) throw new ArgumentOutOfRangeException(nameof(now), "Time must be monotonic.");
        if (!ready)
        {
            if (connected) Clear();
            lastTime = now;
            // A stale session view must not draw actors after disconnect.
            return new RoomSnapshot { Scope = authority.Scope, Tick = authority.Tick,
                TotalShots = authority.TotalShots, TotalHits = authority.TotalHits };
        }
        if (!connected || scope != authority.Scope)
        {
            Clear();
            connected = true;
            scope = authority.Scope;
        }
        else if (authority.Tick < observedTick && latestAuthority != null)
            authority = latestAuthority; // Old network views cannot roll lifecycle or metadata back.
        latestAuthority = Copy(authority);

        // The live view is the only source of membership and lifecycle metadata, even on the same tick.
        var livePlayers = authority.Players.ToDictionary(p => p.PlayerId);
        var liveBullets = authority.Bullets.ToDictionary(b => b.Id);
        foreach (string id in players.Keys.Except(livePlayers.Keys).ToArray()) { players.Remove(id); playerAnchors.Remove(id); PurgePlayer(id); }
        foreach (long id in bullets.Keys.Except(liveBullets.Keys).ToArray()) { bullets.Remove(id); bulletAnchors.Remove(id); PurgeBullet(id); }
        foreach (PlayerPose p in authority.Players)
        {
            bool existing = players.TryGetValue(p.PlayerId, out PlayerPose? old);
            bool reset = existing && (old!.PeerId != p.PeerId || old.Deaths != p.Deaths || old.Health <= 0 && p.Health > 0 ||
                 DistanceSquared(old.X, old.Z, p.X, p.Z) > TeleportDistanceSquared);
            if (reset)
            {
                PurgePlayer(p.PlayerId);
                ResetCount++;
            }
            if (!existing || reset) playerAnchors[p.PlayerId] = new Anchor<PlayerPose>(authority.Tick, Clone(p));
            players[p.PlayerId] = Clone(p);
        }
        foreach (BulletPose b in authority.Bullets)
        {
            bool existing = bullets.TryGetValue(b.Id, out BulletPose? old);
            bool reset = existing && (old!.BornTick != b.BornTick || DistanceSquared(old.X, old.Z, b.X, b.Z) > TeleportDistanceSquared);
            if (reset)
            {
                PurgeBullet(b.Id);
                ResetCount++;
            }
            if (!existing || reset) bulletAnchors[b.Id] = new Anchor<BulletPose>(authority.Tick, Clone(b));
            bullets[b.Id] = Clone(b);
        }

        if (authority.Tick > observedTick)
        {
            observedTick = authority.Tick;
            var sample = new Sample(authority.Tick,
                authority.Players.ToDictionary(p => p.PlayerId, Clone),
                authority.Bullets.ToDictionary(b => b.Id, Clone));
            long jitter = JitterMilliseconds == 0 ? 0 : (long)(NextRandom() % (uint)(JitterMilliseconds * 2 + 1)) - JitterMilliseconds;
            pending.Add(new Delivery(now + DelayMilliseconds + jitter, sample));
            if (pending.Count > Capacity) pending.RemoveAt(0);
        }
        // Reordering is intentional; history is ordered by simulation tick, never by arrival time.
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].Due > now) continue;
            Sample sample = pending[i].Value;
            pending.RemoveAt(i);
            // Validate against CURRENT live lifecycle before admitting any old scheduled pose.
            sample.Players.Keys.ToList().ForEach(id =>
            {
                if (!livePlayers.TryGetValue(id, out PlayerPose? p) || sample.Tick <= playerAnchors[id].Tick ||
                    sample.Players[id].PeerId != p.PeerId || sample.Players[id].Deaths != p.Deaths ||
                    sample.Players[id].Health <= 0 && p.Health > 0) sample.Players.Remove(id);
            });
            sample.Bullets.Keys.ToList().ForEach(id =>
            {
                if (!liveBullets.TryGetValue(id, out BulletPose? b) || sample.Tick <= bulletAnchors[id].Tick ||
                    sample.Bullets[id].BornTick != b.BornTick)
                    sample.Bullets.Remove(id);
            });
            if (deliveredTick != long.MinValue && sample.Tick <= deliveredTick - Capacity) continue;
            int index = history.FindIndex(s => s.Tick >= sample.Tick);
            if (index < 0) history.Add(sample);
            else if (history[index].Tick != sample.Tick) history.Insert(index, sample);
            deliveredTick = Math.Max(deliveredTick, sample.Tick);
            while (history.Count > Capacity) history.RemoveAt(0);
        }
        if (deliveredTick != long.MinValue)
        {
            if (!clockStarted) { renderTick = deliveredTick - BufferMilliseconds * TicksPerMillisecond; clockStarted = true; }
            // Network silence can end while rendering never paused, or a delayed sample may
            // arrive AFTER the first frame following a pause. Rebase only a large backlog;
            // ordinary jitter keeps the monotonic fractional clock.
            if (deliveredTick - renderTick > Math.Max(Capacity / 2d, BufferMilliseconds * TicksPerMillisecond + 2))
                renderTick = deliveredTick - BufferMilliseconds * TicksPerMillisecond;
            // A long pause can jump to the safe buffer region, but never rewind on late delivery.
            if (now - lastTime > 1000) renderTick = Math.Max(renderTick, deliveredTick - BufferMilliseconds * TicksPerMillisecond);
            else renderTick = Math.Min(deliveredTick, renderTick + (now - lastTime) * TicksPerMillisecond);
        }
        lastTime = now;

        bool interpolated = false;
        var frame = new RoomSnapshot
        {
            Scope = authority.Scope, Tick = authority.Tick, TotalShots = authority.TotalShots, TotalHits = authority.TotalHits,
            Players = authority.Players.Select(p =>
            {
                PlayerPose pose = Clone(p);
                if (isHost && p.PlayerId == localPlayerId) return pose;
                (PlayerPose? a, PlayerPose? b, float t) = BracketPlayer(p.PlayerId);
                if (a == null) return pose;
                PlayerPose result = Blend(pose, a, b, t, Enabled);
                if (Enabled && b != null && t > 0 && t < 1 && DistanceSquared(a.X, a.Z, b.X, b.Z) > 0.000001f) interpolated = true;
                return result;
            }).ToArray(),
            Bullets = authority.Bullets.Select(b =>
            {
                BulletPose pose = Clone(b);
                (BulletPose? a, BulletPose? next, float t) = BracketBullet(b.Id);
                if (a == null) return pose;
                if (Enabled && next != null && t > 0 && t < 1 && DistanceSquared(a.X, a.Z, next.X, next.Z) > 0.000001f) interpolated = true;
                pose.X = Blend(a.X, next?.X ?? a.X, Enabled ? t : 0);
                pose.Z = Blend(a.Z, next?.Z ?? a.Z, Enabled ? t : 0);
                return pose;
            }).ToArray()
        };
        if (interpolated) InterpolatedFrames++;
        return frame;
    }

    private (PlayerPose?, PlayerPose?, float) BracketPlayer(string id) => Bracket(playerAnchors[id], s => s.Players.GetValueOrDefault(id));
    private (BulletPose?, BulletPose?, float) BracketBullet(long id) => Bracket(bulletAnchors[id], s => s.Bullets.GetValueOrDefault(id));

    private (T?, T?, float) Bracket<T>(Anchor<T> anchor, Func<Sample, T?> select) where T : class
    {
        T before = anchor.Pose;
        T? after = null;
        long beforeTick = anchor.Tick, afterTick = 0;
        double target = Enabled ? renderTick : double.PositiveInfinity;
        foreach (Sample sample in history)
        {
            if (sample.Tick <= anchor.Tick) continue;
            T? value = select(sample);
            if (value == null) continue;
            if (sample.Tick <= target) { before = value; beforeTick = sample.Tick; }
            else { after = value; afterTick = sample.Tick; break; }
        }
        return (before, after, after == null ? 0 : (float)Math.Clamp((target - beforeTick) / (afterTick - beforeTick), 0, 1));
    }

    private void PurgePlayer(string id)
    {
        foreach (Sample s in history) s.Players.Remove(id);
        foreach (Delivery d in pending) d.Value.Players.Remove(id);
    }
    private void PurgeBullet(long id)
    {
        foreach (Sample s in history) s.Bullets.Remove(id);
        foreach (Delivery d in pending) d.Value.Bullets.Remove(id);
    }
    private uint NextRandom() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random; }
    private static float DistanceSquared(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);
    private static float Blend(float a, float b, float t) => float.IsFinite(a) && float.IsFinite(b) ? (float)((double)a + ((double)b - a) * t) : 0;
    private static PlayerPose Blend(PlayerPose result, PlayerPose a, PlayerPose? b, float t, bool enabled)
    {
        result.X = Blend(a.X, b?.X ?? a.X, enabled ? t : 0);
        result.Z = Blend(a.Z, b?.Z ?? a.Z, enabled ? t : 0);
        // Directions interpolate on the unit circle so opposing aim never collapses to zero.
        float ax = a.AimX, az = a.AimZ, bx = b?.AimX ?? ax, bz = b?.AimZ ?? az;
        float al = MathF.Sqrt(ax * ax + az * az), bl = MathF.Sqrt(bx * bx + bz * bz);
        if (!float.IsFinite(al) || al < 0.00001f) { ax = bx; az = bz; al = bl; }
        if (!float.IsFinite(bl) || bl < 0.00001f) { bx = ax; bz = az; bl = al; }
        if (!float.IsFinite(al) || al < 0.00001f) { result.AimX = 0; result.AimZ = 0; }
        else
        {
            float first = MathF.Atan2(az, ax), second = MathF.Atan2(bz, bx);
            float delta = MathF.Atan2(MathF.Sin(second - first), MathF.Cos(second - first));
            float angle = first + delta * (enabled ? t : 0);
            result.AimX = MathF.Cos(angle); result.AimZ = MathF.Sin(angle);
        }
        return result;
    }
    private static PlayerPose Clone(PlayerPose p) => new() { PlayerId = p.PlayerId, PeerId = p.PeerId, Name = p.Name, X = p.X, Z = p.Z, AimX = p.AimX, AimZ = p.AimZ, Health = p.Health, Deaths = p.Deaths };
    private static BulletPose Clone(BulletPose b) => new() { Id = b.Id, OwnerId = b.OwnerId, BornTick = b.BornTick, X = b.X, Z = b.Z, Vx = b.Vx, Vz = b.Vz };
    private static RoomSnapshot Copy(RoomSnapshot s) => new() { Scope = s.Scope, Tick = s.Tick, TotalShots = s.TotalShots, TotalHits = s.TotalHits, Players = s.Players.Select(Clone).ToArray(), Bullets = s.Bullets.Select(Clone).ToArray() };
    private sealed record Sample(long Tick, Dictionary<string, PlayerPose> Players, Dictionary<long, BulletPose> Bullets);
    private sealed record Delivery(long Due, Sample Value);
    private sealed record Anchor<T>(long Tick, T Pose);
}
