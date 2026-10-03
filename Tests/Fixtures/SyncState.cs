using BITKit.Multiplayer;

namespace Fixture;

public class SyncState
{
    [SyncVar, Hook(nameof(HealthChanged))] public int Health { get; set; } = 100;
    [SyncVar, Hook(nameof(SlotsChanged))] public SyncDictionary<int, int> Slots { get; } = new();
    [SyncVar, Hook(nameof(TasksChanged))] public SyncList<string> Tasks { get; } = new();
    [SyncVar, Hook(nameof(UnlocksChanged))] public SyncHashSet<int> Unlocks { get; } = new();
    public readonly List<(int Old, int New)> HealthEvents = new();
    public readonly List<SyncDictionaryChange<int, int>> SlotEvents = new();
    public readonly List<SyncListChange<string>> TaskEvents = new();
    public readonly List<SyncHashSetChange<int>> UnlockEvents = new();
    public bool ThrowHook;
    public int Calls;
    public string Sender = "";
    private void HealthChanged(int oldValue, int newValue) => HealthEvents.Add((oldValue, newValue));
    private void SlotsChanged(in SyncDictionaryChange<int, int> change)
    { SlotEvents.Add(change); if (ThrowHook) throw new InvalidOperationException("hook failure"); }
    private void TasksChanged(in SyncListChange<string> change) => TaskEvents.Add(change);
    private void UnlocksChanged(in SyncHashSetChange<int> change) => UnlockEvents.Add(change);
    [Rpc(SendTo.Host)] public Task<int> SetSlot(int slot, int count)
    {
        if (!RpcCallContext.TryGetValue(out var context)) throw new InvalidOperationException("Missing sender");
        Sender = context.Sender.Value; Calls++; Slots[slot] = count; Health = count;
        return Task.FromResult(count);
    }
}
public sealed class InheritedSyncState : SyncState { }
public sealed class DtoSyncState
{
    [SyncVar] public SyncDictionary<int, SyncItem> Items { get; } = new();
    [SyncVar] public SyncList<SyncItem> List { get; } = new();
}
public sealed class NullSyncState
{
    [SyncVar] public SyncList<int> Values { get; } = null!;
}
public sealed class SharedSyncState
{
    [SyncVar] public SyncList<int> First { get; }
    [SyncVar] public SyncList<int> Second { get; }
    public SharedSyncState() { First = new SyncList<int>(); Second = First; }
}
// Allocation fixture: no logging or growing event history inside the measured region.
public sealed class SyncPerfState
{
    [SyncVar, Hook(nameof(MapChanged))] public SyncDictionary<int, int> Map { get; } = new();
    [SyncVar] public SyncList<int> List { get; } = new();
    [SyncVar] public SyncHashSet<int> Set { get; } = new();
    [SyncVar] public SyncDictionary<int, SyncItem> Dtos { get; } = new();
    private void MapChanged(in SyncDictionaryChange<int, int> change) { }
}
