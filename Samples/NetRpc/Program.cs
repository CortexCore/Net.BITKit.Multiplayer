using System.Net;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using NetRpc.Sample;
using NetRpc.Sample.Contracts;

var useRelay = args.Contains("--relay");
using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
var accepting = listener.AcceptAsync();
await using var direct = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port);
await using var authorityTransport = await accepting;
Console.WriteLine("Transport admitted.");
using var host = new ServiceCollection().AddSingleton<HealthComponent>().AddNetRpcObject<Actor>()
    .AddNetRpcService<IGameState, GameState>().AddNetRpc(true, _ => authorityTransport, 42).BuildServiceProvider();
var runtime = host.GetRequiredService<RpcContextService>();
Console.WriteLine("Host scope started.");
runtime.Faulted += error => Console.Error.WriteLine(error);
await using var relay = useRelay ? new RelayEndpoint(new IPEndPoint(IPAddress.Loopback, 0), "sample-host") : null;
await using var sidecar = relay != null ? new RelayHostConnection(runtime, "127.0.0.1", relay.EndPoint.Port, "sample-host") : null;
if (sidecar != null) await Until(() => sidecar.IsConnected);
await using var routed = relay != null ? await TcpTransport.ConnectAsync("127.0.0.1", relay.EndPoint.Port) : null;
using var client = new ServiceCollection().AddSingleton<HealthComponent>().AddNetRpcObject<Actor>().AddRemoteInterface<IGameState>()
    .AddNetRpc(false, _ => (ITransport?)routed ?? direct, 42).BuildServiceProvider();
_ = client.GetRequiredService<RpcContextService>();
RegisterPlayer(host); RegisterPlayer(client);
Console.WriteLine("Entities registered.");
var remote = client.GetRequiredService<IGameState>();
Console.WriteLine("Native remote proxy resolved.");
if (await remote.Plus(20, 22) != 42) throw new Exception("Remote interface result mismatch.");
await remote.Damage(10);
var actor = client.GetRequiredService<Actor>(); actor.Fire(5);
if (await actor.Read() != 85) throw new Exception("Woven RPC result mismatch.");
await Until(() => remote.Health == 85 && client.GetRequiredService<HealthComponent>().Value == 85 && remote.Items.Count == 1 && remote.Counts.ContainsKey(10));
host.GetRequiredService<Actor>().Pose(2); await Until(() => actor.Broadcasts == 2);
try { remote.Items.Add(99); throw new Exception("Client authority check failed."); } catch (BITKit.Multiplayer.RpcException) { }
Console.WriteLine($"PASS {(useRelay ? "Relay" : "Direct")}: Plus=42 Health={remote.Health} Items=[{string.Join(',', remote.Items)}] Counts[10]={remote.Counts[10]} Broadcasts={actor.Broadcasts}");

static void RegisterPlayer(ServiceProvider scope)
{
    var entityScope = new ServiceCollection().AddSingleton<INetworkIdentity>(new NetworkIdentity(1))
        .AddSingleton(scope.GetRequiredService<HealthComponent>()).AddSingleton<INetComponent>(p => p.GetRequiredService<HealthComponent>()).BuildServiceProvider();
    scope.GetRequiredService<IEntitiesService>().Register(new NetEntity(entityScope));
}
static async Task Until(Func<bool> condition)
{ using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }
