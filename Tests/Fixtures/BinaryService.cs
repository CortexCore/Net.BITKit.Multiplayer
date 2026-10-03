using BITKit.Multiplayer;

namespace Fixture;

public sealed class BinaryService : IBinaryService
{
    public int Calls;
    public string Sender = "";
    [SyncVar] public BinaryState Current { get; set; } = new();
    [Rpc(SendTo.Host)] public Task<NestedSnapshot> Snapshot(NestedSnapshot snapshot)
    {
        Calls++; Sender = RpcCallContext.Current!.Sender.Value;
        Current = new BinaryState { Scope = snapshot.Scope, Tick = snapshot.Tick,
            Detail = snapshot.Rows.Length == 0 ? new NestedDetail() : snapshot.Rows[0] };
        return Task.FromResult(snapshot);
    }
    [Rpc(SendTo.Host)] public Task<AnnotatedScalar> EchoScalar(AnnotatedScalar value)
    { Calls++; return Task.FromResult(value); }
    [Rpc(SendTo.All)] public void Publish(NestedSnapshot snapshot)
    { Calls++; Sender = RpcCallContext.Current!.Sender.Value; }
}
