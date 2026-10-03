using System.Reflection;
using BITKit.Multiplayer.NetRpc;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class NetworkCollectionHotTests
{
    private static Func<NetMessageReader, bool, long, bool> Applier(object collection)
    {
        var contract = typeof(NetworkCollection).Assembly.GetType("BITKit.Multiplayer.NetRpc.INetworkState")!;
        return (Func<NetMessageReader, bool, long, bool>)contract.GetMethod("Apply")!.CreateDelegate(typeof(Func<NetMessageReader, bool, long, bool>), collection);
    }
    private static NetMessageReader Reader(NetMessageBag bag) => new(bag.Memory, bag.Count);
    [Fact]
    public void ListDeltaPreflightDoesNotCommitTrailingOrInvalidData()
    {
        var list = new NetworkList<int>(); list.Add(5); var apply = Applier(list); int hooks = 0; list.Changed += _ => hooks++;
        using var malformed = NetMessageBag.Pool(); malformed.Write((byte)NetworkOperation.Set); malformed.Write(0); malformed.Write(99); malformed.Write(123);
        Assert.Throws<RpcException>(() => apply(Reader(malformed), false, 2)); Assert.Equal(5, list[0]); Assert.Equal(1, list.Revision); Assert.Equal(0, hooks);
        using var invalid = NetMessageBag.Pool(); invalid.Write((byte)NetworkOperation.Remove); invalid.Write(10);
        Assert.Throws<RpcException>(() => apply(Reader(invalid), false, 2)); Assert.Equal(new[] { 5 }, list);
        using var valid = NetMessageBag.Pool(); valid.Write((byte)NetworkOperation.Set); valid.Write(0); valid.Write(7);
        Assert.True(apply(Reader(valid), false, 2)); Assert.Equal(7, list[0]); Assert.Equal(2, list.Revision); Assert.Equal(1, hooks);
    }
    [Fact]
    public void DictionaryDeltaPreflightRejectsDuplicateMissingAndTrailingWithoutCommit()
    {
        var dict = new NetworkDictionary<int, int>(); dict.Add(1, 5); var apply = Applier(dict); int hooks = 0; dict.Changed += _ => hooks++;
        using var duplicate = NetMessageBag.Pool(); duplicate.Write((byte)NetworkOperation.Add); duplicate.Write(1); duplicate.Write(99);
        Assert.Throws<RpcException>(() => apply(Reader(duplicate), false, 2));
        using var missing = NetMessageBag.Pool(); missing.Write((byte)NetworkOperation.Remove); missing.Write(2);
        Assert.Throws<RpcException>(() => apply(Reader(missing), false, 2));
        using var trailing = NetMessageBag.Pool(); trailing.Write((byte)NetworkOperation.Set); trailing.Write(1); trailing.Write(9); trailing.Write(123);
        Assert.Throws<RpcException>(() => apply(Reader(trailing), false, 2)); Assert.Equal(5, dict[1]); Assert.Equal(1, dict.Revision); Assert.Equal(0, hooks);
        using var valid = NetMessageBag.Pool(); valid.Write((byte)NetworkOperation.Set); valid.Write(1); valid.Write(7);
        Assert.True(apply(Reader(valid), false, 2)); Assert.Equal(7, dict[1]); Assert.Equal(1, hooks);
    }
    [Fact]
    public void ReusedSnapshotBuffersRemainAtomicAndRecoverAfterDecodeFailures()
    {
        var list = new NetworkList<int>(); list.Add(5); var applyList = Applier(list);
        using var shortList = NetMessageBag.Pool(); shortList.Write(2); shortList.Write(9);
        Assert.Throws<RpcException>(() => applyList(Reader(shortList), true, 3)); Assert.Equal(new[] { 5 }, list); Assert.Equal(1, list.Revision);
        using var validList = NetMessageBag.Pool(); validList.Write(2); validList.Write(7); validList.Write(8);
        Assert.True(applyList(Reader(validList), true, 3)); Assert.Equal(new[] { 7, 8 }, list);
        using var again = NetMessageBag.Pool(); again.Write(1); again.Write(42);
        Assert.True(applyList(Reader(again), true, 4)); Assert.Equal(new[] { 42 }, list);
        var dict = new NetworkDictionary<int, int>(); dict.Add(1, 5); var applyDict = Applier(dict);
        using var duplicate = NetMessageBag.Pool(); duplicate.Write(2); duplicate.Write(1); duplicate.Write(8); duplicate.Write(1); duplicate.Write(9);
        Assert.Throws<ArgumentException>(() => applyDict(Reader(duplicate), true, 3)); Assert.Equal(5, dict[1]); Assert.Equal(1, dict.Revision);
        using var validDict = NetMessageBag.Pool(); validDict.Write(1); validDict.Write(2); validDict.Write(42);
        Assert.True(applyDict(Reader(validDict), true, 3)); Assert.False(dict.ContainsKey(1)); Assert.Equal(42, dict[2]);
    }
    [Fact]
    public void ScalarCopyToDoesNotAllocateIntermediateContainers()
    {
        var list = new NetworkList<int>(); var dict = new NetworkDictionary<int, int>(); for (int i = 0; i < 256; i++) { list.Add(i); dict.Add(i, i); }
        var a = new int[256]; var b = new KeyValuePair<int, int>[256]; list.CopyTo(a, 0); dict.CopyTo(b, 0);
        long start = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 100; i++) { list.CopyTo(a, 0); dict.CopyTo(b, 0); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start); Assert.Equal(255, a[255]); Assert.Throws<ArgumentOutOfRangeException>(() => list.CopyTo(a, 1));
    }
}
