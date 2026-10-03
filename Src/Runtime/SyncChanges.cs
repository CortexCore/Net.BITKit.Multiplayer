using System;

namespace BITKit.Multiplayer
{
public enum SyncOperation : byte { None, Add, Insert, Set, Remove, Clear, Reset }
public enum SyncChangeOrigin : byte { Local, Remote, Snapshot }
public delegate void SyncChanged<TChange>(in TChange change) where TChange : struct;

public readonly struct SyncListChange<T>
{
    internal SyncListChange(SyncOperation op, int index = -1, T oldValue = default!, T newValue = default!, long version = 0, SyncChangeOrigin origin = default)
    { Operation = op; Index = index; OldValue = oldValue; NewValue = newValue; Version = version; Origin = origin; }
    public SyncOperation Operation { get; }
    public int Index { get; }
    public T OldValue { get; }
    public T NewValue { get; }
    public long Version { get; }
    public SyncChangeOrigin Origin { get; }
}
public readonly struct SyncDictionaryChange<TKey, TValue>
{
    internal SyncDictionaryChange(SyncOperation op, TKey key = default!, TValue oldValue = default!, TValue newValue = default!, long version = 0, SyncChangeOrigin origin = default)
    { Operation = op; Key = key; OldValue = oldValue; NewValue = newValue; Version = version; Origin = origin; }
    public SyncOperation Operation { get; }
    public TKey Key { get; }
    public TValue OldValue { get; }
    public TValue NewValue { get; }
    public long Version { get; }
    public SyncChangeOrigin Origin { get; }
}
public readonly struct SyncHashSetChange<T>
{
    internal SyncHashSetChange(SyncOperation op, T value = default!, long version = 0, SyncChangeOrigin origin = default)
    { Operation = op; Value = value; Version = version; Origin = origin; }
    public SyncOperation Operation { get; }
    public T Value { get; }
    public long Version { get; }
    public SyncChangeOrigin Origin { get; }
}
}
