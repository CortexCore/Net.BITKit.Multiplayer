using BITKit.Multiplayer;

public sealed class BadAll
{
    [Rpc(SendTo.All)] public Task Event() => Task.CompletedTask;
}
public sealed class BadAsyncVoid
{
    [HostOnly] public async void Guard() => await Task.Yield();
}
public class GenericOuter<T>
{
    public sealed class Nested
    {
        [Rpc(SendTo.Host)] public Task<int> Go() => Task.FromResult(1);
        [SyncVar] public int Value { get; set; }
    }
}
public class MissingSetter
{
    [SyncVar] public int Value { get; }
}
public sealed class OpenMethod
{
    [Rpc(SendTo.Host)] public Task<int> Go<T>(T value) => Task.FromResult(1);
}
public class HidingBase
{
    [SyncVar] public int Value { get; set; }
}
public class HidingChild : HidingBase
{
    [SyncVar] public new int Value { get; set; }
}
public sealed class ReplaceableCollection
{
    [SyncVar] public SyncList<int> Values { get; set; } = new();
}
public sealed class WrongSyncHook
{
    [SyncVar, Hook(nameof(Callback))] public SyncList<int> Values { get; } = new();
    private void Callback(SyncListChange<int> value) { }
}
public sealed class AsyncSyncHook
{
    [SyncVar, Hook(nameof(Callback))] public int Value { get; set; }
    private async void Callback(int before, int after) => await Task.Yield();
}
public sealed class OrphanSyncHook
{
    [Hook(nameof(Callback))] public int Value { get; set; }
    private void Callback(int before, int after) { }
}
public struct UnstableKey { public int Value; }
public sealed class BadSetKey
{
    [SyncVar] public SyncHashSet<UnstableKey> Values { get; } = new();
}
