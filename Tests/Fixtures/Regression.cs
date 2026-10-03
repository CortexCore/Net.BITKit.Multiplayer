using BITKit.Multiplayer;

namespace Fixture;

public sealed class Mapped : ILeft, IRight, IChild, IExplicit, IGeneric<int>
{
    public Mapped() { Version = 3; }
    public int Calls;
    public int FinallyCount;
    public string SenderBefore = "";
    public string SenderAfter = "";
    [SyncVar] public int Version { get; set; }
    [HostOnly] public int HostGuard() => 10;
    [Rpc(SendTo.Host)] public Task<int> Ping(int value) { Calls++; return Task.FromResult(value + 1); }
    [Rpc(SendTo.Host)] public Task<int> Ping(string value) { Calls++; return Task.FromResult(value.Length); }
    [Rpc(SendTo.Host)] public Task<int> Base(int value) { Calls++; return Task.FromResult(value + 2); }
    [Rpc(SendTo.Host)] public Task<int> Echo(int value) { Calls++; return Task.FromResult(value); }
    [Rpc(SendTo.Host)] Task<int> IExplicit.Special(int value) { Calls++; return Task.FromResult(value + 3); }
    [Rpc(SendTo.Host)] public async Task<int> Nested(int value)
    {
        SenderBefore = RpcCallContext.Current!.Sender.Value;
        try
        {
            await Task.Yield();
            if (value < 0) throw new ArgumentException("branch");
            var nested = await Ping(value);
            return nested + 10;
        }
        catch (ArgumentException) { return -1; }
        finally { SenderAfter = RpcCallContext.Current!.Sender.Value; FinallyCount++; }
    }
    [Rpc(SendTo.Host)] public async Task<int> ThrowAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("async failure");
    }
}

public class InheritedBase
{
    public InheritedBase() { State = 4; }
    [SyncVar] public int State { get; set; }
    [Rpc(SendTo.Host)] private Task<int> PrivateBody(int value) => Task.FromResult(value + State);
}
public sealed class InheritedChild : InheritedBase { }
