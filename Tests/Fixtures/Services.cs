using BITKit.Multiplayer;

namespace Fixture;

public sealed class Counter : ICounter
{
    public int Adds;
    public int Announces;
    public string Sender = "";
    [SyncVar] public int Version { get; set; }
    [Rpc(SendTo.Host)] public Task<int> Add(int n) { Adds++; Sender = RpcCallContext.Current!.Sender.Value; Version += n; return Task.FromResult(Version); }
    [Rpc(SendTo.Target)] public Task<int> Read(RpcTarget target, int n) { Sender = RpcCallContext.Current!.Sender.Value; return Task.FromResult(n * 2); }
    [Rpc(SendTo.All)] public void Announce(string message) { Announces++; }
    [Rpc(SendTo.Host)] public Task BroadcastAsync() { Announces++; return Task.CompletedTask; }
    [Rpc(SendTo.Host)] public Task Fail() => throw new ArgumentException("business failure");
    [Rpc(SendTo.Host)] public async Task Slow() { await Task.Delay(1000); }
    [Rpc(SendTo.Host)] public async Task<int> Delayed(int n) { await Task.Delay(500); return n; }
    [HostOnly] public int HostValue() => 7;
}
