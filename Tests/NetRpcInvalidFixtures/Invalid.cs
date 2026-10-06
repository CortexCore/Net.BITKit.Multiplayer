using BITKit.Multiplayer;
using BITKit.Multiplayer.NetRpc;
namespace NetRpcInvalidFixtures;
public sealed class Invalid : IDisposable
{
    public Invalid(IRpcContext<Invalid> rpcContext) { }
    [Rpc(SendTo.Host)] public int Synchronous() => 1;
    [Rpc(SendTo.All)] public Task BroadcastResult() => Task.CompletedTask;
    [Rpc(SendTo.Host, RpcDelivery.Unreliable)] public Task UnreliableResult() => Task.CompletedTask;
    [Rpc(SendTo.Host)] public static void Static() { }
    [Rpc(SendTo.Host)] public void Generic<T>(T value) { }
    [Rpc(SendTo.Host)] public void ByReference(ref int value) { }
    [Rpc(SendTo.Host)] public async void AsyncVoid() { await Task.Yield(); }
    public void Dispose() { }
}

public sealed class MissingDisposable
{
    public MissingDisposable(IRpcContext<MissingDisposable> rpcContext) { }
    [Rpc(SendTo.Host)] public void Fire() { }
}

public sealed class MissingContext : IDisposable
{
    [Rpc(SendTo.Host)] public void Fire() { }
    public void Dispose() { }
}
