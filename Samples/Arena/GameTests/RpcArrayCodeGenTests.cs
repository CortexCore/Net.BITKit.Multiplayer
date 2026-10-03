using BITKit.Multiplayer.CodeGen;
using Mono.Cecil;
using System.Reflection;
using BITKit.Multiplayer;
using MemoryPack;
using Xunit;

namespace BITKit.Multiplayer.Samples.Arena;

public sealed class RpcArrayCodeGenTests
{
    [Fact] public void GeneratedMemoryPackSchemaRoundTripsNestedArenaSnapshot()
    {
        var source = new RoomSnapshot
        {
            Scope = "arena/東京", Tick = 73, TotalShots = 8, TotalHits = 3,
            Players = [
                new PlayerPose { MotionId = 1, PlayerId = "one", PeerId = "peer-1", Name = "Alice", X = 1.25f, Z = -2, AimX = .5f, AimZ = .75f, Health = 75, Deaths = 1 },
                new PlayerPose { MotionId = 2, PlayerId = "two", PeerId = "peer-2", Name = "ボブ", X = -4, Health = 0, Deaths = 2 }
            ],
            Bullets = [new BulletPose { Id = 91, OwnerId = "one", X = 4, Z = 5, Vx = 6, Vz = -7, BornTick = 62 }]
        };
        byte[] bytes = MemoryPackSerializer.Serialize(source);
        RoomSnapshot copy = Assert.IsType<RoomSnapshot>(MemoryPackSerializer.Deserialize<RoomSnapshot>(bytes));
        Assert.Equal(source.Scope, copy.Scope);
        Assert.Equal(source.Tick, copy.Tick);
        Assert.Equal((source.TotalShots, source.TotalHits), (copy.TotalShots, copy.TotalHits));
        Assert.Equal(2, copy.Players.Length);
        Assert.Equal(("one", "peer-1", "Alice", 1, 1.25f, -2f, .5f, .75f, 75, 1),
            (copy.Players[0].PlayerId, copy.Players[0].PeerId, copy.Players[0].Name, copy.Players[0].MotionId,
                copy.Players[0].X, copy.Players[0].Z, copy.Players[0].AimX, copy.Players[0].AimZ, copy.Players[0].Health, copy.Players[0].Deaths));
        Assert.Equal("ボブ", copy.Players[1].Name);
        Assert.Equal(0, copy.Players[1].Health);
        Assert.Equal(2, copy.Players[1].Deaths);
        Assert.Single(copy.Bullets);
        Assert.Equal((91L, "one", 4f, 5f, 6f, -7f, 62L),
            (copy.Bullets[0].Id, copy.Bullets[0].OwnerId, copy.Bullets[0].X,
                copy.Bullets[0].Z, copy.Bullets[0].Vx, copy.Bullets[0].Vz, copy.Bullets[0].BornTick));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncVarRejectsDirectAndNestedCollectionsDespiteRpcArraySupport(bool nested)
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.FullName;
        var original = Path.Combine(bin, "Arena.Game", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "BITKit.Multiplayer.Samples.Arena.Game.unwoven.dll");
        var scratch = Path.Combine(Path.GetTempPath(), "arena-weave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var input = Path.Combine(scratch, "input.dll");
            using (var assembly = ModuleDefinition.ReadModule(original))
            {
                var health = assembly.Types.Single(t => t.Name == "PlayerHealth").Properties.Single(p => p.Name == "Health");
                health.PropertyType = nested ? assembly.ImportReference(typeof(RoomSnapshot)) : new ArrayType(assembly.TypeSystem.Int32);
                assembly.Write(input);
            }
            var errors = Weaver.Weave(input, Path.Combine(scratch, "output.dll"));
            Assert.Contains(errors, e => e.Contains("SyncVar requires supported scalar auto-property"));
        }
        finally { Directory.Delete(scratch, true); }
    }
}
