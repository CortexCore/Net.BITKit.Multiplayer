using BITKit.Multiplayer;
using Cysharp.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using NetRpc.Sample.Contracts;

namespace NetRpc.Sample;

public sealed class HealthComponent : NetComponent<int>
{
    public HealthComponent() : base(1, 100) { }
}
public sealed class Actor : IDisposable
{
    private readonly HealthComponent _health;
    public Actor(HealthComponent health, IRpcContext<Actor> rpcContext) => _health = health;
    public int Broadcasts { get; private set; }
    [Rpc(SendTo.Host)] public void Fire(int damage) => _health.Value -= damage;
    [Rpc(SendTo.Host)] public UniTask<int> Read() => UniTask.FromResult(_health.Value);
    [Rpc(SendTo.All, RpcDelivery.Unreliable)] public void Pose(int count) => Broadcasts += count;
    public void Dispose() { }
}
public sealed class GameState : IGameState
{
    private readonly HealthComponent _health;
    private readonly Actor _actor;
    public GameState(HealthComponent health, Actor actor) { _health = health; _actor = actor; }
    public int Health => _health.Value;
    public IList<int> Items { get; } = new NetworkList<int>();
    public IDictionary<int, int> Counts { get; } = new NetworkDictionary<int, int>();
    public UniTask<int> Plus(int a, int b) => UniTask.FromResult(a + b);
    public UniTask Damage(int damage) { _actor.Fire(damage); Items.Add(damage); Counts[damage] = 1; return UniTask.CompletedTask; }
}
