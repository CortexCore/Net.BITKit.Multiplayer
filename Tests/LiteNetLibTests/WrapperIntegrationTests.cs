using System.Collections.Concurrent;
using BITKit.Multiplayer.LiteNetLibDirect;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.Samples.NetRpcGodot;
using Xunit;

namespace BITKit.Multiplayer.LiteNetLibTests;

// Adapter-only cases and helpers are linked from the independent extension's Tests/DirectTests.cs.
public partial class DirectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_before_attach_notifies_late_runtime_and_wrapper_subscribers(bool wrapped)
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        await p.Host.DisposeAsync();
        using var wrapper = wrapped ? new ImpairedTransport(p.Host, p.Host, new NetworkImpairment(), new ConcurrentQueue<Exception>()) : null;
        ITransportLifetime lifetime = wrapper == null ? p.Host : wrapper;
        int notified = 0;
        lifetime.Closed += () => Interlocked.Increment(ref notified);
        await p.Host.DisposeAsync();
        Assert.Equal(1, notified);
        var runtime = new RpcContextService(new EmptyServices(), true);
        int detached = 0; runtime.PeerDisconnected += _ => detached++;
        runtime.AttachPeer(2, wrapper == null ? p.Host : wrapper);
        Assert.Equal(1, detached);
        runtime.Dispose();
    }
    [Fact]
    public async Task Actual_wrapper_preserves_pre_subscription_frames_in_order()
    {
        var p = await Pair(); await using var server = p.Server; await using var client = p.Client;
        // Valid opaque frames: the sample's impairment decoder inspects kind only.
        byte[] Frame(uint sequence) => NetRpcCodec.Encode(new NetRpcModel(NetRpcMessageKind.Call, 1, sequence, 0, 0, ReadOnlyMemory<byte>.Empty));
        await p.Remote.Send(Frame(1)); await p.Remote.Send(Frame(2));
        await Until(() => EarlyCount(p.Host) == 2);
        using var wrapper = new ImpairedTransport(p.Host, p.Host, new NetworkImpairment(), new ConcurrentQueue<Exception>());
        Assert.Equal(2, EarlyCount(p.Host));
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<uint>();
        wrapper.OnReceived += data => { values.Add(NetRpcCodec.Decode(data).MethodId); if (values.Count == 3) got.TrySetResult(); };
        await p.Remote.Send(Frame(3));
        await got.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(new uint[] { 1, 2, 3 }, values);
    }
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
