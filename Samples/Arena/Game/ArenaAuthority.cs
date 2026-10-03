using BITKit.Multiplayer;

namespace BITKit.Multiplayer.Samples.Arena;

// All operations, including callbacks from the socket and tick loop, are serialized by Gate.
public sealed class ArenaAuthority
{
    public readonly object Gate = new();
    internal readonly Dictionary<string, Player> Players = new(StringComparer.Ordinal);
    internal readonly List<BulletPose> Bullets = new();
    public long TickNumber { get; private set; }
    public int Shots { get; private set; }
    public int Hits { get; private set; }
    private long _bulletId;
    private int _motionId;
    public sealed class Player
    {
        public string Id = "", Peer = "", Name = "";
        public float X, Z, SpawnX, SpawnZ, AimX = 1, AimZ, MoveX, MoveZ;
        public int Health = 100, Deaths;
        public long RespawnTick, LastShotTick = -100;
        public uint MoveSequence, ShotSequence;
        public int MotionId;
    }
    public bool Add(string id, string peer, string name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(id) || Players.ContainsKey(peer) || Players.Values.Any(p => p.Id == id) || Players.Count >= 64) return false;
            var slot = Players.Count;
            Players[peer] = new Player { Id = id, Peer = peer, Name = name, MotionId = ++_motionId, X = slot % 2 == 0 ? -4 : 4, Z = (slot / 2) * 2, SpawnX = slot % 2 == 0 ? -4 : 4, SpawnZ = (slot / 2) * 2, AimX = slot % 2 == 0 ? 1 : -1 };
            return true;
        }
    }
    public void Remove(string peer)
    { lock (Gate) { if (Players.Remove(peer, out var player)) Bullets.RemoveAll(b => b.OwnerId == player.Id); } }
    private static bool Finite(float a, float b) => float.IsFinite(a) && float.IsFinite(b);
    private static (float, float) Normalize(float x, float z)
    {
        var length = MathF.Sqrt(x * x + z * z);
        return length > 1 ? (x / length, z / length) : (x, z);
    }
    public bool Move(string peer, float x, float z, uint sequence)
    {
        lock (Gate)
        {
            if (!Finite(x, z) || !Players.TryGetValue(peer, out var p) || p.Health == 0 || sequence <= p.MoveSequence || sequence == 0) return false;
            p.MoveSequence = sequence;
            (p.MoveX, p.MoveZ) = Normalize(Math.Clamp(x, -1, 1), Math.Clamp(z, -1, 1));
            return true;
        }
    }
    public BulletPose? Fire(string peer, float x, float z, uint sequence)
    {
        lock (Gate)
        {
            if (!Finite(x, z) || !Players.TryGetValue(peer, out var p) || p.Health == 0 || sequence <= p.ShotSequence || sequence == 0) return null;
            p.ShotSequence = sequence;
            var len = MathF.Sqrt(x * x + z * z);
            if (!float.IsFinite(len) || len < .001f || TickNumber - p.LastShotTick < 5 || Bullets.Count >= 256) return null;
            p.LastShotTick = TickNumber;
            p.AimX = x / len; p.AimZ = z / len;
            var bullet = new BulletPose { Id = ++_bulletId, OwnerId = p.Id, X = p.X + p.AimX * .65f, Z = p.Z + p.AimZ * .65f, Vx = p.AimX * 14, Vz = p.AimZ * 14, BornTick = TickNumber };
            Bullets.Add(bullet); Shots++;
            return Copy(bullet);
        }
    }
    public long[] Step(Action<string, int>? healthChanged = null)
    {
        var changes = new List<(string Peer, int Health)>();
        long[] result;
        lock (Gate)
        {
            TickNumber++;
            foreach (var p in Players.Values)
            {
                if (p.Health == 0)
                {
                    if (TickNumber >= p.RespawnTick) { p.Health = 100; p.MotionId = ++_motionId; p.X = p.SpawnX; p.Z = p.SpawnZ; changes.Add((p.Peer, p.Health)); }
                    continue;
                }
                p.X = Math.Clamp(p.X + p.MoveX * .2f, -ArenaProtocol.HalfExtent, ArenaProtocol.HalfExtent);
                p.Z = Math.Clamp(p.Z + p.MoveZ * .2f, -ArenaProtocol.HalfExtent, ArenaProtocol.HalfExtent);
            }
            var ended = new List<long>();
            for (var i = Bullets.Count - 1; i >= 0; i--)
            {
                var b = Bullets[i]; var oldX = b.X; var oldZ = b.Z;
                b.X += b.Vx * .05f; b.Z += b.Vz * .05f;
                var remove = Math.Abs(b.X) > 13 || Math.Abs(b.Z) > 13 || TickNumber - b.BornTick > 50;
                foreach (var p in Players.Values)
                {
                    if (remove || p.Id == b.OwnerId || p.Health == 0) continue;
                    var dx = b.X - oldX; var dz = b.Z - oldZ;
                    var t = Math.Clamp(((p.X - oldX) * dx + (p.Z - oldZ) * dz) / (dx * dx + dz * dz), 0, 1);
                    if (MathF.Pow(oldX + dx * t - p.X, 2) + MathF.Pow(oldZ + dz * t - p.Z, 2) > .55f * .55f) continue;
                    p.Health = Math.Max(0, p.Health - 25); Hits++; remove = true;
                    if (p.Health == 0) { p.Deaths++; p.RespawnTick = TickNumber + 60; p.MoveX = p.MoveZ = 0; }
                    changes.Add((p.Peer, p.Health));
                }
                if (remove) { ended.Add(b.Id); Bullets.RemoveAt(i); }
            }
            result = ended.ToArray();
        }
        foreach (var change in changes) healthChanged?.Invoke(change.Peer, change.Health);
        return result;
    }
    public RoomSnapshot Snapshot(string scope)
    {
        lock (Gate) return new RoomSnapshot { Scope = scope, Tick = TickNumber, Players = Players.Values.Select(p => new PlayerPose { MotionId = p.MotionId, PlayerId = p.Id, PeerId = p.Peer, Name = p.Name, X = p.X, Z = p.Z, AimX = p.AimX, AimZ = p.AimZ, Health = p.Health, Deaths = p.Deaths }).ToArray(), Bullets = Bullets.Select(Copy).ToArray(), TotalShots = Shots, TotalHits = Hits };
    }
    // Called under Gate; the network hot path never constructs a presentation snapshot.
    public (int Players, int Bullets, long Tick) FillMotion(PlayerMotionUpdate[] players, BulletMotionUpdate[] bullets)
    {
        lock (Gate)
        {
            int count = 0;
            foreach (var p in Players.Values)
                players[count++] = new PlayerMotionUpdate { Id = p.MotionId, X = p.X, Z = p.Z, AimX = p.AimX, AimZ = p.AimZ };
            int bulletCount = 0;
            foreach (var b in Bullets)
                bullets[bulletCount++] = new BulletMotionUpdate { Id = b.Id, X = b.X, Z = b.Z };
            return (count, bulletCount, TickNumber);
        }
    }
    public static BulletPose Copy(BulletPose b) => new() { Id = b.Id, OwnerId = b.OwnerId, X = b.X, Z = b.Z, Vx = b.Vx, Vz = b.Vz, BornTick = b.BornTick };
}
