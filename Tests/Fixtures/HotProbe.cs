using BITKit.Multiplayer;

namespace Fixture;

public sealed class HotProbe : IHotProbe
{
    public int Count;
    public string LastSender = "";
    public HotProbe? Nested;
    public Task<int>? ChildTask;
    public string OuterBefore = "", OuterAfter = "", SyncObserved = "", ChildBefore = "", ChildAfter = "", ThrowObserved = "";
    [Rpc(SendTo.Host)] public void Tick(int value)
    {
        if (!RpcCallContext.TryGetValue(out var context)) throw new InvalidOperationException("Missing call context");
        Interlocked.Add(ref Count, value);
        LastSender = context.Sender.Value;
    }
    [Rpc(SendTo.Host)] public void Poses(ArraySegment<AnnotatedScalar> values)
    {
        if (values.Count == 0) return;
        Interlocked.Add(ref Count, values[0].Value);
    }
    [Rpc(SendTo.Host)] public void Scalar(AnnotatedScalar value) => Interlocked.Add(ref Count, value.Value);
    [Rpc(SendTo.Host, Delivery = RpcDelivery.Unreliable)] public void UdpTick(int value) => Interlocked.Add(ref Count, value);
    [Rpc(SendTo.Host, Delivery = RpcDelivery.Unreliable)] public void UdpScalar(AnnotatedScalar value) => Interlocked.Add(ref Count, value.Value);
    [Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)] public void UdpBroadcast(int value) => Interlocked.Add(ref Count, value);
    [Rpc(SendTo.Host)] public Task<int> Echo(int value) => Task.FromResult(value + 1);
    [Rpc(SendTo.Host)] public void Reenter(int value)
    {
        if (!RpcCallContext.TryGetValue(out var before)) throw new InvalidOperationException("Missing outer context");
        Tick(value); // nested woven call, Host executes locally and restores outer value context
        if (!RpcCallContext.TryGetValue(out var after) || !before.Sender.Equals(after.Sender))
            throw new InvalidOperationException("Nested call corrupted outer sender");
    }
    [Rpc(SendTo.All)] public void Broadcast(int value) => Interlocked.Add(ref Count, value);
    [Rpc(SendTo.Target)] public void Target(RpcTarget target, int value)
    {
        LastSender = RpcCallContext.TryGetValue(out var context) ? context.Sender.Value : "missing";
        Interlocked.Add(ref Count, value);
    }
    [Rpc(SendTo.Host)] public async Task<int> AsyncParent()
    {
        var before = RpcCallContext.Current ?? throw new InvalidOperationException("Missing async parent");
        OuterBefore = before.Sender.Value + ":" + before.Target.Service;
        await Task.Yield();
        Nested!.SyncNested();
        var childResult = await (Nested.ChildTask ?? throw new InvalidOperationException("Missing child Task"));
        try { Nested.SyncThrows(); }
        catch (InvalidOperationException) { }
        var after = RpcCallContext.Current ?? throw new InvalidOperationException("Lost async parent");
        OuterAfter = after.Sender.Value + ":" + after.Target.Service;
        if (!RpcCallContext.TryGetValue(out var valueContext) || !valueContext.Sender.Equals(after.Sender) || !valueContext.Target.Equals(after.Target))
            throw new InvalidOperationException("Outer value context not restored");
        return childResult + 1;
    }
    [Rpc(SendTo.Host)] public void SyncNested()
    {
        if (!RpcCallContext.TryGetValue(out var frame)) throw new InvalidOperationException("Missing sync frame");
        var legacy = RpcCallContext.Current ?? throw new InvalidOperationException("Missing legacy sync context");
        if (!legacy.Sender.Equals(frame.Sender) || !legacy.Target.Equals(frame.Target))
            throw new InvalidOperationException("Async parent masked nested sync context");
        SyncObserved = frame.Sender.Value + ":" + frame.Target.Service;
        ChildTask = Nested!.AsyncChild();
        if (!RpcCallContext.TryGetValue(out var restored) || !restored.Target.Equals(frame.Target))
            throw new InvalidOperationException("Async child masked resumed sync context");
    }
    [Rpc(SendTo.Host)] public async Task<int> AsyncChild()
    {
        var before = RpcCallContext.Current ?? throw new InvalidOperationException("Missing async child");
        ChildBefore = before.Sender.Value + ":" + before.Target.Service;
        await Task.Yield();
        var after = RpcCallContext.Current ?? throw new InvalidOperationException("Lost async child continuation");
        ChildAfter = after.Sender.Value + ":" + after.Target.Service;
        if (!RpcCallContext.TryGetValue(out var current) || !current.Target.Equals(after.Target))
            throw new InvalidOperationException("Sync frame leaked into async child continuation");
        return 41;
    }
    [Rpc(SendTo.Host)] public void SyncThrows()
    {
        ThrowObserved = (RpcCallContext.Current?.Sender.Value ?? "none") + ":" + (RpcCallContext.Current?.Target.Service ?? "none");
        throw new InvalidOperationException("intentional sync nested failure");
    }
}
