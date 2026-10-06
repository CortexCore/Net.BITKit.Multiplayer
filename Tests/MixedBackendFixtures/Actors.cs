using BITKit.Multiplayer;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;

namespace MixedBackendFixtures;

public static class RuntimeReference
{
    public static readonly Type Core = typeof(BITKit.Multiplayer.NetRpc.RpcContextService);
}

public sealed class LegacyActor
{
    [Rpc(SendTo.Host)] public Task<int> Add(int value) => Task.FromResult(value + 1);
}

[NetRpcBackend]
public sealed class NewActor : IDisposable
{
    public NewActor(IRpcContext<NewActor> rpcContext) { }
    [Rpc(SendTo.Host)] public UniTask<int> Add(int value) => UniTask.FromResult(value + 1);
    public void Dispose() { }
}
