using BITKit.Multiplayer;
using Cysharp.Threading.Tasks;

namespace NetRpcFixtures;

public interface IActor
{
    void Fire(int damage);
    Task<int> Read(int add);
    UniTask<int> Plus(int a, int b);
    UniTask Wait();
    ValueTask<int> Value(int number);
}
public sealed class Actor : IActor
{
    public int Damage { get; private set; }
    public int Broadcasts { get; private set; }
    [Rpc(SendTo.Host)] public void Fire(int damage) => Damage += damage;
    [Rpc(SendTo.Host)] public async Task<int> Read(int add) { await Task.Yield(); return Damage + add; }
    [Rpc(SendTo.Host)] public async UniTask<int> Plus(int a, int b) { await UniTask.Yield(); return a + b; }
    [Rpc(SendTo.Host)] public UniTask Wait() => UniTask.CompletedTask;
    [Rpc(SendTo.Host)] public ValueTask<int> Value(int number) => ValueTask.FromResult(number);
    [Rpc(SendTo.All)] public void Notify(int value) => Broadcasts += value;
    [Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)] public void Pose(int value) => Broadcasts += value;
    [Rpc(SendTo.Host)] public Task Fault() => Task.FromException(new InvalidOperationException("woven fault"));
    [Rpc(SendTo.Host)] public async Task<int> Nested(int amount) { Fire(amount); return await Read(1); }
    [Rpc(SendTo.Host)] public Task LocalReference(int[] values) { values[0]++; return Task.CompletedTask; }
    [Rpc(SendTo.All)] public void BroadcastReference(int[] values) { values[0]++; Broadcasts++; }
}
