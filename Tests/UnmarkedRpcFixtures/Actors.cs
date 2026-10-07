using BITKit.Multiplayer;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;

namespace UnmarkedRpcFixtures;

public sealed class TaskActor : IDisposable
{
    public TaskActor(IRpcContext<TaskActor> context) { }
    [Rpc(SendTo.Host)] public Task<int> Add(int value) => Task.FromResult(value + 1);
    public void Dispose() { }
}

public sealed class UniTaskActor : IDisposable
{
    public UniTaskActor(IRpcContext<UniTaskActor> context) { }
    [Rpc(SendTo.Host)] public UniTask<int> Add(int value) => UniTask.FromResult(value + 1);
    public void Dispose() { }
}
