using BITKit.Multiplayer;
namespace NetRpcInvalidFixtures;
public sealed class Invalid
{
    [Rpc(SendTo.Host)] public int Synchronous() => 1;
    [Rpc(SendTo.All)] public Task BroadcastResult() => Task.CompletedTask;
    [Rpc(SendTo.Host, RpcDelivery.Unreliable)] public Task UnreliableResult() => Task.CompletedTask;
    [Rpc(SendTo.Host)] public static void Static() { }
    [Rpc(SendTo.Host)] public void Generic<T>(T value) { }
    [Rpc(SendTo.Host)] public void ByReference(ref int value) { }
    [Rpc(SendTo.Host)] public async void AsyncVoid() { await Task.Yield(); }
}
