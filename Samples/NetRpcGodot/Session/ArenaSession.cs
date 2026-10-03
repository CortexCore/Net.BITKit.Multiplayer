using System.Collections.Concurrent;
using System.Reflection;
using Cysharp.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.LiteNetLibDirect;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.Samples.NetRpcGodot;

public sealed class ArenaHost : IDisposable
{
    public ServiceProvider Services { get; }
    public RpcContextService Runtime { get; }
    public ArenaWorld World => Services.GetRequiredService<ArenaWorld>();
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public ArenaHost()
    {
        var services = new ServiceCollection().AddSingleton<IEntitiesService, EntitiesService>();
        services.AddSingleton(p => new ArenaWorld(p.GetRequiredService<IEntitiesService>(), true));
        services.AddNetRpcService<IArena, ArenaService>().AddNetRpcObject<CombatCommands>();
        services.AddSingleton(p =>
        {
            var runtime = new RpcContextService(p, true, ArenaRules.Scope);
            runtime.AttachEntities(p.GetRequiredService<IEntitiesService>());
            runtime.AllowContract(typeof(IArena)); runtime.AllowContract(typeof(CombatCommands));
            runtime.PeerDisconnected += peer => p.GetRequiredService<ArenaWorld>().PeerLeft(peer);
            runtime.StartSynchronization(new NetRpcOptions { SyncInterval = TimeSpan.FromMilliseconds(50), SnapshotInterval = TimeSpan.FromSeconds(1) });
            return runtime;
        });
        services.AddSingleton<IRemoteInterfaceFactory, PrecompiledRemoteInterfaceFactory>();
        Services = services.BuildServiceProvider(); Runtime = Services.GetRequiredService<RpcContextService>(); Runtime.Faulted += error => Errors.Enqueue(error);
        _ = Services.GetRequiredService<ArenaService>(); _ = Services.GetRequiredService<CombatCommands>();
    }
    public void Dispose() => Services.Dispose();
}

public sealed class ArenaClient : IAsyncDisposable
{
    private readonly IAsyncDisposable _transportOwner;
    private readonly ImpairedTransport _impaired;
    private readonly ServiceProvider _services;
    private int _disposed;
    public int Slot { get; private set; }
    public bool Connected { get; private set; } = true;
    public IArena Remote { get; }
    public CombatCommands Combat { get; }
    public ArenaWorld World => _services.GetRequiredService<ArenaWorld>();
    public NetworkImpairment Impairment { get; }
    public ConcurrentQueue<Exception> Errors { get; } = new();
    private ArenaClient(BITKit.Multiplayer.NetRpc.ITransport transport, ITransportLifetime lifetime, IAsyncDisposable owner, NetworkImpairment impairment)
    {
        _transportOwner = owner; Impairment = impairment; _impaired = new ImpairedTransport(transport, lifetime, impairment, Errors);
        var services = new ServiceCollection().AddSingleton<IEntitiesService, EntitiesService>();
        services.AddSingleton(p => new ArenaWorld(p.GetRequiredService<IEntitiesService>(), false));
        services.AddRemoteInterface<IArena>().AddNetRpcObject<CombatCommands>().AddNetRpc(false, _ => _impaired, ArenaRules.Scope);
        _services = services.BuildServiceProvider(); var runtime = _services.GetRequiredService<RpcContextService>();
        runtime.PeerDisconnected += _ => Connected = false; runtime.Faulted += error => Errors.Enqueue(error);
        _ = World; Combat = _services.GetRequiredService<CombatCommands>(); Remote = _services.GetRequiredService<IArena>();
    }
    public static async UniTask<ArenaClient> ConnectAsync(string host, int port, string name, int slot, NetworkImpairment? impairment = null, CancellationToken cancellationToken = default, bool liteNetLib = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        IAsyncDisposable owner;
        BITKit.Multiplayer.NetRpc.ITransport transport;
        ITransportLifetime lifetime;
        if (liteNetLib)
        {
            var connected = await LiteNetLibEndpoint.ConnectAsync(host, port, timeout.Token);
            owner = connected.Endpoint; transport = connected.Transport; lifetime = connected.Transport;
        }
        else { var tcp = await TcpTransport.ConnectAsync(host, port, cancellationToken: timeout.Token); owner = tcp; transport = tcp; lifetime = tcp; }
        ArenaClient? client = null;
        try
        {
            client = new ArenaClient(transport, lifetime, owner, impairment ?? new NetworkImpairment());
            client.Slot = await client.Remote.Join(name, slot).AttachExternalCancellation(timeout.Token); return client;
        }
        catch { if (client != null) await client.DisposeAsync(); else await owner.DisposeAsync(); throw; }
    }
    public bool UsesWovenCombat => typeof(CombatCommands).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Any(m => m.Name.StartsWith("__netrpc_recv_"));
    public ValueTask DisposeAsync() => new ValueTask(DisposeUniTaskAsync().AsTask());
    public async UniTask DisposeUniTaskAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Connected = false; _impaired.Dispose(); _services.Dispose(); await _transportOwner.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class ClientProof
{
    public int Slot { get; set; }
    public bool Passed { get; set; }
    public bool RealGodot { get; set; }
    public bool WovenCombat { get; set; }
    public bool HostAuthority { get; set; }
    public bool UnauthorizedDenied { get; set; }
    public bool Movement { get; set; }
    public bool Pickup { get; set; }
    public bool Attack { get; set; }
    public bool DisconnectObserved { get; set; }
    public bool Reconnected { get; set; }
    public bool MonotonicRevisions { get; set; }
    public long DroppedDatagrams { get; set; }
    public long ReorderedDatagrams { get; set; }
    public long RecoveredCollectionGaps { get; set; }
    public int Health2 { get; set; }
    public int Inventory101 { get; set; }
    public int Score1 { get; set; }
    public int Tick { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>Shared assertions run inside actual Godot Client processes as well as any headless .NET client harness.</summary>
public static class ArenaAutomation
{
    public static async UniTask<ClientProof> Run(Func<UniTask<ArenaClient>> connect, int slot, Action<ArenaClient?> display, CancellationToken cancellationToken)
    {
        var proof = new ClientProof { Slot = slot, MonotonicRevisions = true };
        ArenaClient? client = null;
        try
        {
            client = await connect(); display(client); proof.WovenCombat = client.UsesWovenCombat;
            await Until(() => client.Remote.ConnectedPlayers.Count == 2, cancellationToken);
            try { client.World.Players[slot - 1].Health.Value = 999; }
            catch (RpcException error) when (error.Error == RpcError.InvalidRole) { proof.HostAuthority = true; }
            if (!proof.HostAuthority || !proof.WovenCombat) throw new Exception("Authority or actual woven assembly assertion failed.");
            var settings = client.Impairment; settings.LossPercent = 25; settings.LatencyMs = 60; settings.Reorder = true;
            var revisions = new long[2];
            bool Check()
            {
                for (int i = 0; i < 2; i++) { long revision = client.World.Players[i].Position.Revision; if (revision < revisions[i]) proof.MonotonicRevisions = false; revisions[i] = revision; }
                return proof.MonotonicRevisions;
            }
            if (slot == 1)
            {
                while (client.World.Players[0].Position.Value.X < 245)
                { client.Remote.Input(1, 0.25f, 0); Check(); await Task.Delay(100, cancellationToken); }
                client.Remote.Input(1, 0, 0); proof.Movement = true;
                settings.DropNextEventDelta = true;
                proof.Pickup = await client.Remote.Pickup(1, 1);
                if (!proof.Pickup) throw new Exception("Host rejected pickup after movement; replica X=" + client.World.Players[0].Position.Value.X);
                proof.Attack = await client.Combat.Attack(1, 2) == 80;
                await Until(() => client.World.Players[1].Health.Value == 80 && client.Remote.Inventory.ContainsKey(101) && client.Remote.Events.Any(e => e.StartsWith("HIT")) && Check(), cancellationToken);
                proof.RecoveredCollectionGaps = settings.DroppedDeltas;
                proof.DroppedDatagrams = settings.DroppedDatagrams; proof.ReorderedDatagrams = settings.ReorderedDatagrams;
                await Task.Delay(1200, cancellationToken); display(null); await client.DisposeAsync(); client = null;
                await Task.Delay(700, cancellationToken);
                client = await connect(); display(client); proof.Reconnected = await client.Remote.Ping(42) == 42;
                await Until(() => client.Remote.ConnectedPlayers.Count == 2 && client.World.Players[1].Health.Value == 80 && client.Remote.Inventory.ContainsKey(101), cancellationToken);
            }
            else
            {
                await Until(() => client.World.Players[0].Position.Value.X >= 240 && Check(), cancellationToken); proof.Movement = true;
                await Until(() => client.World.Players[1].Health.Value == 80 && client.Remote.Inventory.ContainsKey(101) && Check(), cancellationToken);
                proof.Pickup = proof.Attack = true;
                try { await client.Combat.Attack(1, 2); }
                catch (RpcException error) when (error.Error == RpcError.Unauthorized) { proof.UnauthorizedDenied = true; }
                await Until(() => !client.Remote.ConnectedPlayers.Contains(1), cancellationToken); proof.DisconnectObserved = true;
                await Until(() => client.Remote.ConnectedPlayers.Contains(1), cancellationToken);
                proof.DroppedDatagrams = settings.DroppedDatagrams; proof.ReorderedDatagrams = settings.ReorderedDatagrams;
            }
            proof.Health2 = client.World.Players[1].Health.Value;
            proof.Inventory101 = client.Remote.Inventory.TryGetValue(101, out var count) ? count : 0;
            proof.Score1 = client.Remote.Scores[1]; proof.Tick = client.Remote.Tick;
            proof.Passed = proof.HostAuthority && proof.WovenCombat && proof.Movement && proof.Pickup && proof.Attack && proof.MonotonicRevisions
                && proof.Health2 == 80 && proof.Inventory101 == 1 && proof.Score1 == 10 && (slot == 1 ? proof.Reconnected && proof.RecoveredCollectionGaps > 0 : proof.DisconnectObserved && proof.UnauthorizedDenied);
            if (!proof.Passed) throw new Exception("Final replicated state or lifecycle proof failed.");
            // Keep both peers present until the other process has committed its final assertions.
            await Task.Delay(slot == 1 ? 1000 : 1500, cancellationToken);
        }
        catch (Exception error) { proof.Error = error.ToString(); proof.Passed = false; }
        finally { display(null); if (client != null) await client.DisposeAsync(); }
        return proof;
    }
    public static async UniTask Until(Func<bool> condition, CancellationToken token)
    { while (!condition()) { token.ThrowIfCancellationRequested(); await Task.Delay(20, token); } }
}
