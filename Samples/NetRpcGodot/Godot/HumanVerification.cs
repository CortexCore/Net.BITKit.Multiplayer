using System.Reflection;
using BITKit.Multiplayer;
using BITKit.Multiplayer.Samples.NetRpcGodot.Human;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

// 验收放在独立文件；阅读人类案例时无需从这里开始。
internal static class HumanVerification
{
    public static async UniTask<string> Run(ServiceProvider services, bool observer, CancellationToken token)
    {
        var shop = services.GetRequiredService<IWorkshop>();
        var actions = services.GetRequiredService<DummyActions>();
        var dummy = services.GetRequiredService<TrainingDummy>();
        if (!typeof(DummyActions).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Any(m => m.Name.StartsWith("__netrpc_recv_")))
            throw new Exception("DummyActions was not actually woven.");
        if (!observer)
        {
            if (await shop.Plus(20, 22) != 42 || await shop.AddCoins(10) != 10 || !await shop.BuyApple() || await actions.Damage(20) != 80)
                throw new Exception("Host RPC result mismatch.");
            if (await shop.BroadcastPulse() != 1) throw new Exception("Host pulse mismatch.");
        }
        await Until(() => shop.Coins == 7 && shop.Inventory.TryGetValue("apple", out int count) && count == 1 &&
            shop.Messages.Count == 2 && dummy.Health.Value == 80 && dummy.LastPulse == 1 && dummy.PulseCount == 1, token);
        bool denied = false;
        try { dummy.Health.Value = 999; } catch (RpcException error) when (error.Error == RpcError.InvalidRole) { denied = true; }
        if (!denied || dummy.Health.Value != 80) throw new Exception("Client state authority failed.");
        return $"role={(observer ? "observer" : "actor")}; proxy={shop.GetType().Name}; woven=True; coins=7; apples=1; messages=2; HP=80; UDP-All=1; client-write-denied=True";
    }
    private static async UniTask Until(Func<bool> condition, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { token.ThrowIfCancellationRequested(); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Human example state did not converge."); await Task.Delay(20, token); }
    }
}
