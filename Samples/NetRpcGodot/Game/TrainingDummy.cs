using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.Samples.NetRpcGodot.Human;

// ③ 双方都有同一个靶子 Entity；只同步组件，不自动生成/销毁游戏对象。
public sealed class TrainingDummy : IDisposable
{
    private readonly object _gate = new();
    private readonly IEntitiesService _entities;
    private readonly ServiceProvider _components;
    private readonly NetEntity _entity;
    private int _lastPulse, _pulseCount;

    public NetComponent<int> Health { get; } = new(componentId: 1, initialValue: 100);
    public int LastPulse => Volatile.Read(ref _lastPulse);
    public int PulseCount => Volatile.Read(ref _pulseCount);

    public TrainingDummy(IEntitiesService entities)
    {
        _entities = entities;
        _components = new ServiceCollection()
            .AddSingleton<INetworkIdentity>(new NetworkIdentity(entityId: 1))
            .AddSingleton<INetComponent>(Health)
            .BuildServiceProvider();
        _entity = new NetEntity(_components);
        _entities.Register(_entity);
    }

    public int Damage(int amount)
    {
        if (amount < 1 || amount > 100) throw new ArgumentOutOfRangeException(nameof(amount));
        lock (_gate)
        {
            Health.Value = Math.Max(0, Health.Value - amount);
            Console.WriteLine($"[HOST] Dummy damaged by {amount}; HP = {Health.Value}");
            return Health.Value;
        }
    }

    public void ReceivePulse(int pulse)
    {
        Volatile.Write(ref _lastPulse, pulse);
        Interlocked.Increment(ref _pulseCount);
        Console.WriteLine($"[THIS PEER] Received UDP pulse {pulse}");
    }

    public void Dispose() { _entities.Unregister(_entity); _components.Dispose(); }
}

// 同一个普通类也能联网。Client 调用 Damage，编织器转发；Host 执行原业务体。
public sealed class DummyActions : IDisposable
{
    private readonly TrainingDummy _dummy;
    public DummyActions(TrainingDummy dummy, IRpcContext<DummyActions> rpcContext) => _dummy = dummy;

    [Rpc(SendTo.Host)]
    public UniTask<int> Damage(int amount) => UniTask.FromResult(_dummy.Damage(amount));

    // Host 调用：自己一次，每个 Client 一次。UDP 不可靠，因此没有返回值。
    [Rpc(SendTo.All, RpcDelivery.Unreliable)]
    public void Pulse(int pulse) => _dummy.ReceivePulse(pulse);

    public void Dispose() { }
}
