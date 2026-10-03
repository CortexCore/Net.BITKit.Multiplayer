using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;

namespace BITKit.Multiplayer.Samples.NetRpcGodot.Human;

// ② Host 真正执行的业务。没有 socket、发包、方法 ID 或 Godot 类型。
public sealed class Workshop : IWorkshop
{
    private readonly object _gate = new();
    private readonly DummyActions _actions;
    private int _coins, _pulse;

    public Workshop(DummyActions actions) => _actions = actions;
    public int Coins { get { lock (_gate) return _coins; } }
    public IList<string> Messages { get; } = new NetworkList<string>();
    public IDictionary<string, int> Inventory { get; } = new NetworkDictionary<string, int>();

    public UniTask<int> Plus(int a, int b)
    {
        int result = checked(a + b);
        Console.WriteLine($"[HOST] Plus({a}, {b}) = {result}");
        return UniTask.FromResult(result);
    }

    public UniTask<int> AddCoins(int amount)
    {
        if (amount < 1 || amount > 100) throw new ArgumentOutOfRangeException(nameof(amount), "Use 1..100 coins.");
        lock (_gate)
        {
            _coins = checked(_coins + amount);
            Record($"领取 {amount} 金币，现在共有 {_coins} 金币");
            return UniTask.FromResult(_coins);
        }
    }

    public UniTask<bool> BuyApple()
    {
        lock (_gate)
        {
            if (_coins < 3) return UniTask.FromResult(false);
            _coins -= 3;
            Inventory["apple"] = Inventory.TryGetValue("apple", out int count) ? count + 1 : 1;
            Record($"花 3 金币买了一个苹果，剩余 {_coins} 金币");
            return UniTask.FromResult(true);
        }
    }

    public UniTask<int> BroadcastPulse()
    {
        int pulse;
        lock (_gate) pulse = ++_pulse;
        _actions.Pulse(pulse);
        return UniTask.FromResult(pulse);
    }

    private void Record(string message)
    {
        if (Messages.Count >= 8) Messages.RemoveAt(0);
        Messages.Add(message);
        Console.WriteLine("[HOST] " + message);
    }
}
