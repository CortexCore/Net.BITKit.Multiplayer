using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.Samples.NetRpcGodot;

public sealed class ArenaPlayer
{
    public int Slot { get; }
    public NetComponent<Position> Position { get; }
    public NetComponent<int> Health { get; } = new(2, 100);
    internal float InputX, InputY;
    internal long LastInput;
    internal uint Owner;
    internal ServiceProvider Provider;
    internal NetEntity Entity;
    internal ArenaPlayer(int slot, IEntitiesService entities)
    {
        Slot = slot; Position = new NetComponent<Position>(1, ArenaRules.Spawn(slot));
        Provider = new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity((uint)slot))
            .AddSingleton<INetComponent>(Position).AddSingleton<INetComponent>(Health).BuildServiceProvider();
        Entity = new NetEntity(Provider); entities.Register(Entity);
    }
}

/// <summary>Engine-independent world. Only the Host mutates authority; clients contain the same two predefined entities.</summary>
public sealed class ArenaWorld : IDisposable
{
    private readonly object _gate = new();
    private readonly IEntitiesService _entities;
    private readonly bool _host;
    private int _tick;
    public ArenaPlayer[] Players { get; }
    public NetworkList<int> ConnectedPlayers { get; } = new();
    public NetworkList<int> AvailablePickups { get; } = new();
    public NetworkList<string> Events { get; } = new();
    public NetworkDictionary<int, int> Scores { get; } = new();
    public NetworkDictionary<int, int> Inventory { get; } = new();
    public ArenaWorld(IEntitiesService entities, bool host)
    {
        _entities = entities; _host = host;
        Players = new[] { new ArenaPlayer(1, entities), new ArenaPlayer(2, entities) };
        if (host) { foreach (int id in new[] { 1, 2, 3 }) AvailablePickups.Add(id); Scores[1] = Scores[2] = 0; }
    }
    public int Tick { get { lock (_gate) return _tick; } }
    private static uint Sender => NetRpcCallContext.Current?.SenderPeerId ?? throw new RpcException(RpcError.Unauthorized, "No authenticated call context.");
    private ArenaPlayer Own(int slot)
    {
        if (!_host || slot < 1 || slot > 2 || Players[slot - 1].Owner != Sender)
            throw new RpcException(RpcError.Unauthorized, "Player slot belongs to another connection.");
        return Players[slot - 1];
    }
    public int Join(string name, int requestedSlot)
    {
        lock (_gate)
        {
            if (!_host || string.IsNullOrWhiteSpace(name) || name.Length > 24) throw new RpcException(RpcError.InvalidPayload, "Name must be 1..24 characters.");
            var current = Players.FirstOrDefault(p => p.Owner == Sender); if (current != null) return current.Slot;
            var player = Players.FirstOrDefault(p => p.Owner == 0 && (requestedSlot == 0 || p.Slot == requestedSlot))
                ?? throw new RpcException(RpcError.LimitExceeded, "The requested player slot is occupied.");
            player.Owner = Sender; player.InputX = player.InputY = 0; player.Health.Value = 100; player.Position.Value = ArenaRules.Spawn(player.Slot);
            ConnectedPlayers.Add(player.Slot); Record($"JOIN P{player.Slot} {name}"); return player.Slot;
        }
    }
    public void SetInput(int slot, float x, float y)
    {
        lock (_gate)
        {
            var player = Own(slot);
            if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1 || Math.Abs(y) > 1) throw new RpcException(RpcError.InvalidPayload, "Input is not a bounded direction.");
            float length = MathF.Sqrt(x * x + y * y); if (length > 1) { x /= length; y /= length; }
            player.InputX = x; player.InputY = y; player.LastInput = Environment.TickCount64;
        }
    }
    public int Attack(int slot, int target)
    {
        lock (_gate)
        {
            var attacker = Own(slot);
            if (target < 1 || target > 2 || target == slot || attacker.Health.Value == 0) throw new RpcException(RpcError.InvalidPayload, "Invalid attack target.");
            var victim = Players[target - 1];
            if (victim.Owner == 0 || Distance(attacker.Position.Value, victim.Position.Value) > ArenaRules.AttackRange) throw new RpcException(RpcError.Unauthorized, "Target is outside attack range.");
            victim.Health.Value = Math.Max(0, victim.Health.Value - 20); Scores[slot] += 10;
            Record($"HIT P{slot} -> P{target}: {victim.Health.Value} HP"); return victim.Health.Value;
        }
    }
    public bool Pickup(int slot, int id)
    {
        lock (_gate)
        {
            var player = Own(slot);
            if (player.Health.Value == 0 || !AvailablePickups.Contains(id) || Distance(player.Position.Value, ArenaRules.PickupPosition(id)) > ArenaRules.PickupRange) return false;
            AvailablePickups.Remove(id); int key = slot * 100 + id;
            Inventory[key] = Inventory.TryGetValue(key, out var count) ? count + 1 : 1;
            Record($"LOOT P{slot} item {id}"); Record($"BAG P{slot} key {key} = {Inventory[key]}"); return true;
        }
    }
    public bool Respawn(int slot)
    {
        lock (_gate) { var player = Own(slot); player.Health.Value = 100; player.Position.Value = ArenaRules.Spawn(slot); player.InputX = player.InputY = 0; Record($"RESPAWN P{slot}"); return true; }
    }
    public void Advance(float seconds)
    {
        lock (_gate)
        {
            if (!_host) return; _tick++;
            foreach (var player in Players)
            {
                if (player.Owner == 0 || player.Health.Value <= 0) continue;
                if (Environment.TickCount64 - player.LastInput > 500) player.InputX = player.InputY = 0;
                var position = player.Position.Value;
                position.X = Math.Clamp(position.X + player.InputX * ArenaRules.Speed * seconds, 16, ArenaRules.Width - 16);
                position.Y = Math.Clamp(position.Y + player.InputY * ArenaRules.Speed * seconds, 16, ArenaRules.Height - 16);
                player.Position.Value = position;
            }
        }
    }
    public void PeerLeft(uint peer)
    {
        lock (_gate)
        {
            var player = Players.FirstOrDefault(p => p.Owner == peer); if (player == null) return;
            player.Owner = 0; player.InputX = player.InputY = 0; ConnectedPlayers.Remove(player.Slot); Record($"LEFT P{player.Slot}");
        }
    }
    private void Record(string message) { if (Events.Count >= 64) Events.RemoveAt(0); Events.Add(message); }
    public static float Distance(Position a, Position b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    public void Dispose() { foreach (var player in Players) { _entities.Unregister(player.Entity); player.Provider.Dispose(); } }
}

public sealed class ArenaService : IArena
{
    private readonly ArenaWorld _world;
    public ArenaService(ArenaWorld world) => _world = world;
    public UniTask<int> Join(string name, int requestedSlot) => UniTask.FromResult(_world.Join(name, requestedSlot));
    public void Input(int player, float x, float y) => _world.SetInput(player, x, y);
    public UniTask<bool> Pickup(int player, int pickup) => UniTask.FromResult(_world.Pickup(player, pickup));
    public UniTask<bool> Respawn(int player) => UniTask.FromResult(_world.Respawn(player));
    public UniTask<int> Ping(int nonce) => UniTask.FromResult(nonce);
    public int Tick => _world.Tick;
    public IList<int> ConnectedPlayers => _world.ConnectedPlayers;
    public IList<int> AvailablePickups => _world.AvailablePickups;
    public IList<string> Events => _world.Events;
    public IDictionary<int, int> Scores => _world.Scores;
    public IDictionary<int, int> Inventory => _world.Inventory;
}

/// <summary>Actual ordinary-class IL Wrapper, distinct from the source-generated interface surface.</summary>
public sealed class CombatCommands
{
    private readonly ArenaWorld _world;
    public CombatCommands(ArenaWorld world) => _world = world;
    [Rpc(SendTo.Host)] public UniTask<int> Attack(int player, int target) => UniTask.FromResult(_world.Attack(player, target));
}
