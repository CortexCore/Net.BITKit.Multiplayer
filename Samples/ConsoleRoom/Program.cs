using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using System.Net;
using System.Net.Sockets;

// Real TCP/DMTP loopback. This process holds two distinct room runtimes, not a dual-role Host.
var epoch = Guid.NewGuid().ToString("N");
var hostPeer = new PeerId("host-" + epoch);
var clientPeer = new PeerId("client-" + epoch);
using var hostWire = new TouchSocketHostWire();
using var clientWire = new TouchSocketClientWire(hostPeer);
var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
await hostWire.StartAsync(port, "bitkit-multiplayer-sample");
await clientWire.ConnectAsync("127.0.0.1", port, "bitkit-multiplayer-sample");
// An application performs authenticated admission here. The sample explicitly trusts its local client.
hostWire.Admit(clientPeer, clientWire.SessionId);
using var host = new RpcRuntime(NetworkRole.Host, "demo-" + epoch, hostPeer, hostPeer, hostWire);
using var client = new RpcRuntime(NetworkRole.Client, "demo-" + epoch, clientPeer, hostPeer, clientWire);
host.RegisterMember(new RoomMember(clientPeer, playerId: "local-demo-player", ready: true));
client.ConfirmReady();
using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
while (!client.IsReady)
    await Task.Delay(10, readyTimeout.Token); // Wait for the Host's authoritative directory over the real socket.
var key = new TargetKey("counter");
host.Bind(key, new Counter(), _ => true);
var service = client.CreateProxy<ICounter>(key, new Dictionary<string, SendTo> { [RpcRuntime.MethodId(typeof(ICounter).GetMethod(nameof(ICounter.Add))!)] = SendTo.Host });
Console.WriteLine("Host result: " + await service.Add(2));
Console.WriteLine("Transport: TouchSocket TCP/DMTP; Host and Client use separate runtime instances.");

public interface ICounter { Task<int> Add(int delta); }
public sealed class Counter : ICounter
{
    [Rpc(SendTo.Host)] public Task<int> Add(int delta) => Task.FromResult(delta + 40);
}
