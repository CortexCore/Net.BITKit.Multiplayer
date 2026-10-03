using System.Net;
using System.Net.Sockets;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;

// Two ordinary objects, two separate runtimes, actual TCP/DMTP sockets and build-time weaving.
int port = FreePort();
var hostPeer = new PeerId("host"); var clientPeer = new PeerId("client");
var scope = Guid.NewGuid().ToString("N"); var key = new TargetKey("inventory");
using var hw = new TouchSocketHostWire(); using var cw = new TouchSocketClientWire(hostPeer);
await hw.StartAsync(port, "local-sync-demo", IPAddress.Loopback);
await cw.ConnectAsync("127.0.0.1", port, "local-sync-demo");
// This localhost demonstration trusts its sole client. A real application verifies admission first.
hw.Admit(clientPeer, cw.SessionId);
using var host = new RpcRuntime(NetworkRole.Host, scope, hostPeer, hostPeer, hw);
using var client = new RpcRuntime(NetworkRole.Client, scope, clientPeer, hostPeer, cw);
host.UnhandledDispatch += e => Console.Error.WriteLine(e);
client.UnhandledDispatch += e => Console.Error.WriteLine(e);
host.RegisterMember(new RoomMember(clientPeer)); client.ConfirmReady();
await Until(() => client.IsReady);
var authority = new InventoryState("Host"); var replica = new InventoryState("Client");
authority.Items[1] = 5;
authority.Tasks.AddRange(new[] { "Find supplies", "Return to base" });
authority.Unlocked.UnionWith(new[] { 10, 20 });
host.Bind(key, authority, owner: clientPeer);
var initial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
client.Synchronized += target => { if (target.Equals(key)) initial.TrySetResult(true); };
client.Bind(key, replica);
await initial.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine($"Initial snapshot: items[1]={replica.Items[1]}, tasks={replica.Tasks.Count}, unlocked={replica.Unlocked.Count}");
int result = await replica.AddItems(1, 2);
// The RPC result does not imply that every asynchronous state frame has already applied.
await Until(() => replica.Items[1] == result && replica.RevisionCount == 1);
Console.WriteLine($"RPC result={result}; replicated items[1]={replica.Items[1]}; sender verified by Host");
try { replica.Items[1] = 999; throw new Exception("Client write should have failed"); }
catch (RpcException e) when (e.Error == RpcError.InvalidRole) { Console.WriteLine("Client direct mutation correctly rejected."); }
Console.WriteLine("Sync collections demo passed.");

static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(10, timeout.Token);
}
static int FreePort()
{
    for (int attempt = 0; attempt < 32; attempt++)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            probe.Start(); int candidate = ((IPEndPoint)probe.LocalEndpoint).Port;
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, candidate)); return candidate;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { }
        finally { probe.Stop(); }
    }
    throw new IOException("No shared TCP/UDP port available");
}

public sealed class InventoryState
{
    private readonly string label;
    public InventoryState(string label) => this.label = label;
    [SyncVar, Hook(nameof(ItemsChanged))]
    public SyncDictionary<int, int> Items { get; } = new(64);
    [SyncVar] public SyncList<string> Tasks { get; } = new();
    [SyncVar] public SyncHashSet<int> Unlocked { get; } = new();
    [SyncVar, Hook(nameof(CountChanged))] public int RevisionCount { get; private set; }

    [Rpc(SendTo.Host)]
    public Task<int> AddItems(int slot, int amount)
    {
        // Bind(owner: clientPeer) already enforces real sender ownership.
        if (!RpcCallContext.TryGetValue(out var caller)) throw new InvalidOperationException("Missing RPC context");
        if (slot < 0 || slot >= 64 || amount < 1 || amount > 10) throw new ArgumentOutOfRangeException(nameof(amount));
        Items.TryGetValue(slot, out int old);
        Items[slot] = checked(old + amount); RevisionCount++;
        Console.WriteLine($"Host accepted {caller.Sender}: +{amount} at slot {slot}");
        return Task.FromResult(Items[slot]);
    }
    private void ItemsChanged(in SyncDictionaryChange<int, int> change) =>
        Console.WriteLine($"{label} Items: {change.Operation}, v{change.Version}, {change.Origin}");
    private void CountChanged(int before, int after) => Console.WriteLine($"{label} RevisionCount: {before} -> {after}");
}
