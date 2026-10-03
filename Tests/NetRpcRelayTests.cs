using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcRelayTests
{
    public sealed class RelayMath { [Rpc(SendTo.Host)] public Task<int> Echo(int value) => Task.FromResult(value); }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    [Fact]
    public async Task TransparentRelayCarriesTcpRepliesAndUdpSnapshotsAndDirectStillWorksAfterRelayFailure()
    {
        await using var relay = new RelayEndpoint(new IPEndPoint(IPAddress.Loopback, 0), "host-key");
        var (directClient, directHost) = NetRpcDesignTests.Pair.Create();
        using var host = NetRpcDesignTests.Host(directHost, 17); var hr = host.GetRequiredService<RpcContextService>();
        await using var sidecar = new RelayHostConnection(hr, "127.0.0.1", relay.EndPoint.Port, "host-key");
        await Until(() => sidecar.IsConnected);
        await using var link = await TcpTransport.ConnectAsync("127.0.0.1", relay.EndPoint.Port);
        using var client = NetRpcDesignTests.Client(link, 17); var cr = client.GetRequiredService<RpcContextService>(); var proxy = client.GetRequiredService<NetRpcDesignTests.IGame>();
        Assert.Equal(42, await proxy.Plus(20, 22));
        hr.RegisterTarget(0xFA, new RelayMath());
        using (var arguments = NetMessageBag.Pool())
        {
            arguments.Write(5); var method = RpcContextService.RpcMethodId(typeof(RelayMath), typeof(RelayMath).GetMethod("Echo")!);
            Assert.Equal(5, await cr.CreateContext(0xFA).Request<int>(new NetRpcModel(NetRpcMessageKind.Call, 0xFA, method, 0, arguments.Count, arguments.Memory)));
        }
        var hc = host.GetRequiredService<NetRpcDesignTests.Game>().HealthComponent; var cc = new NetComponent<int>(1);
        host.GetRequiredService<IEntitiesService>().Register(new NetEntity(new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(7)).AddSingleton<INetComponent>(hc).BuildServiceProvider()));
        client.GetRequiredService<IEntitiesService>().Register(new NetEntity(new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(7)).AddSingleton<INetComponent>(cc).BuildServiceProvider()));
        await proxy.Damage(10); await hr.PublishStateAsync(); await Until(() => cc.Value == 90 && proxy.Health == 90 && proxy.Items.Count == 1);
        Assert.Equal(1, proxy.Counts[10]);
        await sidecar.DisposeAsync();
        using var direct = NetRpcDesignTests.Client(directClient, 17); Assert.Equal(42, await direct.GetRequiredService<NetRpcDesignTests.IGame>().Plus(40, 2));
    }
    [Fact]
    public async Task HostSidecarRetriesWhenRelayAppearsLater()
    {
        using var port = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0)); var endpoint = port.EndPoint; port.Dispose();
        var (a, b) = NetRpcDesignTests.Pair.Create(); using var host = NetRpcDesignTests.Host(b); var runtime = host.GetRequiredService<RpcContextService>();
        await using var sidecar = new RelayHostConnection(runtime, "127.0.0.1", endpoint.Port, "key");
        using var direct = NetRpcDesignTests.Client(a); Assert.Equal(42, await direct.GetRequiredService<NetRpcDesignTests.IGame>().Plus(20, 22));
        await using var relay = new RelayEndpoint(endpoint, "key"); await Until(() => sidecar.IsConnected);
        await using var link = await TcpTransport.ConnectAsync("127.0.0.1", endpoint.Port); using var client = NetRpcDesignTests.Client(link);
        Assert.Equal(42, await client.GetRequiredService<NetRpcDesignTests.IGame>().Plus(40, 2));
        await relay.DisposeAsync(); await Until(() => !sidecar.IsConnected);
        await using var replacement = new RelayEndpoint(endpoint, "key"); await Until(() => sidecar.IsConnected);
        await using var reconnected = await TcpTransport.ConnectAsync("127.0.0.1", endpoint.Port);
        using var newClient = NetRpcDesignTests.Client(reconnected);
        Assert.Equal(42, await newClient.GetRequiredService<NetRpcDesignTests.IGame>().Plus(41, 1));
    }
}
