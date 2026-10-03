using Xunit;

namespace BITKit.Multiplayer.Tests;

public class NetworkTimeTests
{
    [Fact]
    public void AuthorityUsesMonotonicSessionSecondsAndStopsWithSession()
    {
        double local = 100;
        var clock = new NetworkTime(true, () => local);
        Assert.True(clock.IsSynchronized); Assert.Equal(0, clock.time);
        local += 3; Assert.Equal(3, clock.Time);
        clock.Stop(); local += 50;
        Assert.False(clock.IsSynchronized); Assert.Equal(3, clock.time);
    }
    [Fact]
    public void ClientCompensatesHalfRttRejectsInvalidSamplesAndNeverRewinds()
    {
        double local = 1000;
        var clock = new NetworkTime(false, () => local);
        Assert.False(clock.IsSynchronized); Assert.Equal(0, clock.time);
        long sequence = clock.BeginSample(); local += .2;
        clock.AcceptSample(sequence + 1, 999); clock.AcceptSample(sequence, double.NaN);
        Assert.False(clock.IsSynchronized);
        clock.AcceptSample(sequence, 42);
        Assert.True(clock.IsSynchronized); Assert.Equal(42.1, clock.time, 6); Assert.Equal(.2, clock.RoundTripTime, 6);
        local += .5; double before = clock.time;
        sequence = clock.BeginSample(); local += .1; clock.AcceptSample(sequence, 42);
        Assert.True(clock.time >= before);
        local += 2; Assert.True(clock.time > before);
        clock.Stop(); double frozen = clock.time; local += 10;
        Assert.Equal(frozen, clock.time); Assert.False(clock.IsSynchronized);
    }
    [Fact]
    public void OnlyOneSampleWaitsUntilResponseOrTimeout()
    {
        double local = 0; var clock = new NetworkTime(false, () => local);
        long first = clock.BeginSample(); local = 2;
        Assert.Equal(0, clock.BeginSample());
        local = 6; long second = clock.BeginSample(); Assert.True(second > first);
        clock.AcceptSample(first, 123); Assert.False(clock.IsSynchronized);
        local = 6.2; clock.AcceptSample(second, 42); Assert.True(clock.IsSynchronized);
    }
    private sealed class Wire : IRoomWire
    {
        public Wire Other = null!;
        public PeerId Identity;
        public bool IsConnected => true;
        public event Action<PeerId, byte[]>? Received;
        public event Action<PeerId>? PeerLeft;
        public Task SendAsync(PeerId peer, byte[] bytes) { Other.Received?.Invoke(Identity, bytes); return Task.CompletedTask; }
        public void Leave(PeerId peer) => PeerLeft?.Invoke(peer);
        public void Dispose() { }
    }
    [Fact]
    public async Task RuntimeSynchronizesAfterAdmissionAndInvalidatesOnHostLoss()
    {
        var h = new PeerId("host"); var c = new PeerId("client");
        var hw = new Wire { Identity = h }; var cw = new Wire { Identity = c }; hw.Other = cw; cw.Other = hw;
        using var host = new RpcRuntime(NetworkRole.Host, "clock", h, h, hw);
        using var client = new RpcRuntime(NetworkRole.Client, "clock", c, h, cw);
        host.RegisterMember(new RoomMember(c)); client.ConfirmReady();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!client.NetworkTime.IsSynchronized) await Task.Delay(10, timeout.Token);
        Assert.InRange(Math.Abs(host.NetworkTime.time - client.NetworkTime.time), 0, .1);
        cw.Leave(h); Assert.False(client.NetworkTime.IsSynchronized);
        double stopped = client.NetworkTime.time; await Task.Delay(20);
        Assert.Equal(stopped, client.NetworkTime.time);
    }
}
