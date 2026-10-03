using System.Text;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class RoomTransportHubTests
{
    [Fact]
    public async Task OneRoomCanAttachDirectRelayReplayAndBotAndRouteByPeer()
    {
        using var hub = new RoomTransportHub();
        using var direct = new VirtualRoomTransport();
        using var relay = new VirtualRoomTransport();
        using var replay = new VirtualRoomTransport();
        using var bot = new VirtualRoomTransport();
        var directBinding = hub.Attach("direct", RoomTransportKind.Direct, direct, defaultRoute: true);
        using var relayBinding = hub.Attach("relay", RoomTransportKind.Relay, relay);
        using var replayBinding = hub.Attach("replay", RoomTransportKind.Replay, replay);
        using var botBinding = hub.Attach("bot", RoomTransportKind.Bot, bot);
        var relayPeer = new PeerId("relay-peer"); var botPeer = new PeerId("bot-peer");
        relayBinding.Bind(relayPeer); botBinding.Bind(botPeer);
        var relaySeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var botSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.SentReliable += (_, _, payload) => relaySeen.TrySetResult(Encoding.UTF8.GetString(payload.Span));
        bot.SentReliable += (_, _, payload) => botSeen.TrySetResult(Encoding.UTF8.GetString(payload.Span));
        await hub.SendReliableAsync(relayPeer, 3, Encoding.UTF8.GetBytes("relay"));
        await hub.SendReliableAsync(botPeer, 4, Encoding.UTF8.GetBytes("bot"));
        Assert.Equal("relay", await relaySeen.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("bot", await botSeen.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        directBinding.Bind(new PeerId("direct-peer"));
        Assert.True(hub.IsConnected);
    }

    [Fact]
    public void VirtualTransportInjectsReplayFramesThroughTheSameSurface()
    {
        using var replay = new VirtualRoomTransport();
        var peer = new PeerId("replay-peer");
        var received = string.Empty;
        replay.MemoryReceived += (_, bytes) => received = Encoding.UTF8.GetString(bytes.Span);
        replay.InjectReliable(peer, 0, Encoding.UTF8.GetBytes("recorded-rpc"));
        Assert.Equal("recorded-rpc", received);
    }

    [Fact]
    public async Task BotAndReplayCanInjectAndEgressWithoutSocketSpecificLogic()
    {
        using var hub = new RoomTransportHub();
        using var replay = new VirtualRoomTransport();
        using var bot = new VirtualRoomTransport();
        var replayPeer = new PeerId("replay"); var botPeer = new PeerId("bot");
        hub.Attach("replay", RoomTransportKind.Replay, replay, true).Bind(replayPeer);
        hub.Attach("bot", RoomTransportKind.Bot, bot).Bind(botPeer);
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.SentReliable += (_, channel, bytes) => { if (channel == 7) sent.TrySetResult(System.Text.Encoding.UTF8.GetString(bytes.Span)); };
        await hub.SendReliableAsync(botPeer, 7, System.Text.Encoding.UTF8.GetBytes("bot-command"));
        replay.InjectReliable(replayPeer, 0, System.Text.Encoding.UTF8.GetBytes("replay-rpc"));
        Assert.Equal("bot-command", await sent.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(hub.IsConnected);
    }
}
