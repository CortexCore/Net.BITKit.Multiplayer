using System;
using System.Text;
using System.Threading.Tasks;
using BITKit.Multiplayer;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class RoomForwardingTests
{
    [Fact]
    public async Task ReliableAndUnreliableApplicationChannelsShareOneWireWithoutHidingRpcFrames()
    {
        using var raw = new LoopbackWire();
        using var wire = new RoomTransportHub();
        wire.Attach("virtual", RoomTransportKind.Replay, raw, true);
        var peer = new PeerId("peer");
        byte reliableChannel = 0;
        byte unreliableChannel = 0;
        string reliable = "", unreliable = "", native = "";
        wire.ReliableReceived += (_, channel, bytes) => { reliableChannel = channel; reliable = System.Text.Encoding.UTF8.GetString(bytes.Span); };
        wire.ForwardedUnreliableReceived += (_, channel, bytes) => { unreliableChannel = channel; unreliable = System.Text.Encoding.UTF8.GetString(bytes.Span); };
        wire.MemoryReceived += (_, bytes) => native = System.Text.Encoding.UTF8.GetString(bytes.Span);

        await wire.SendReliableAsync(peer, 3, Encoding.UTF8.GetBytes("reliable"));
        await wire.SendUnreliableAsync(peer, 4, Encoding.UTF8.GetBytes("unreliable"));
        raw.EmitReliable(peer, Encoding.UTF8.GetBytes("rpc"));

        Assert.Equal((byte)3, reliableChannel);
        Assert.Equal("reliable", reliable);
        Assert.Equal((byte)4, unreliableChannel);
        Assert.Equal("unreliable", unreliable);
        Assert.Equal("rpc", native);
    }

    [Fact]
    public async Task ApplicationChannelZeroIsReserved()
    {
        using var wire = new RoomTransportHub();
        wire.Attach("virtual", RoomTransportKind.Replay, new LoopbackWire(), true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            wire.SendReliableAsync(new PeerId("peer"), 0, Encoding.UTF8.GetBytes("reserved")));
    }

    private sealed class LoopbackWire : IRoomWire, IRoomMemoryWire, IRoomDatagrams
    {
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId, ReadOnlyMemory<byte>>? MemoryReceived;
        public event Action<PeerId>? PeerLeft { add { } remove { } }
        public event Action<PeerId, ReadOnlyMemory<byte>>? UnreliableReceived;
        public int MaxUnreliablePayloadBytes => 1200;
        public bool UnreliableEnabled { get; set; } = true;
        public bool IsUnreliableReady(PeerId peer) => true;
        public DatagramStatistics GetDatagramStatistics() => new();
        public Task RebindDatagramsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(PeerId peer, byte[] data) { Received?.Invoke(peer, data); return Task.CompletedTask; }
        public ValueTask SendMemoryAsync(PeerId peer, ReadOnlyMemory<byte> payload) { MemoryReceived?.Invoke(peer, payload); return ValueTask.CompletedTask; }
        public Task SendUnreliableAsync(PeerId peer, ReadOnlyMemory<byte> payload) { UnreliableReceived?.Invoke(peer, payload); return Task.CompletedTask; }
        public void EmitReliable(PeerId peer, byte[] payload) => MemoryReceived?.Invoke(peer, payload);
        public void Dispose() { }
    }
}
