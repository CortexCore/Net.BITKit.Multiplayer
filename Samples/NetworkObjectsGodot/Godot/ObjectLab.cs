using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Environment = System.Environment;

namespace BITKit.Multiplayer.Samples.NetworkObjectsGodot;

/// <summary>One real Godot process per peer. Files are test barriers; object state travels over TCP/UDP.</summary>
public partial class ObjectLab : Node
{
    private const string PrefabAddress = "demo/crate";
    private const string SceneKey = "demo/room/scene-crate";
    private const ulong Scope = 202610060071;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<TcpTransport> _connections = new();
    private readonly List<GodotTransport> _engineTransports = new();
    private readonly List<SpawnEvidence> _spawnEvidence = new();
    private GodotThread _main = null!;
    private GodotNetworkObjectAdapter _adapter = null!;
    private ServiceProvider? _services;
    private RpcContextService _runtime = null!;
    private IEntitiesService _entities = null!;
    private NetworkObjectService _objects = null!;
    private TcpTransportListener? _listener;
    private ReplicatedCrate _scene = null!;
    private ReplicatedCrate? _dynamic;
    private Label _status = null!;
    private string _phase = "Starting", _directory = "", _role = "";
    private uint _peer;
    private int _port, _spawned, _despawning, _holdSeconds, _phaseSeconds;
    private Task? _accept;
    private bool _testFinished;
    private ObjectProof _proof = null!;

    public override void _Ready()
    {
        _main = new GodotThread();
        var args = OS.GetCmdlineUserArgs();
        _role = Argument(args, "--role", "client");
        _peer = uint.Parse(Argument(args, "--peer", _role == "host" ? "1" : "2"));
        _port = int.Parse(Argument(args, "--port", "28930"));
        _holdSeconds = int.Parse(Argument(args, "--hold-seconds", "0"));
        _phaseSeconds = int.Parse(Argument(args, "--phase-seconds", "0"));
        _directory = Path.GetFullPath(Argument(args, "--results", "../results/manual"));
        Directory.CreateDirectory(_directory);
        _lifetime.CancelAfter(TimeSpan.FromSeconds(int.Parse(Argument(args, "--timeout", "60"))));
        _proof = new ObjectProof { Role = _role, Peer = _peer, ProcessId = Environment.ProcessId,
            Engine = Engine.GetVersionInfo()["string"].AsString(), RealGodot = true };
        GetWindow().Title = $"BITKit Network Objects · {_role} peer {_peer}";
        _scene = GetNode<ReplicatedCrate>("SceneCrate");
        BuildView();
        _adapter = new GodotNetworkObjectAdapter(this, _main);
        _adapter.RegisterPrefab(PrefabAddress, GD.Load<PackedScene>("res://ReplicatedCrate.tscn"));
        // Peer 3 joins after both objects exist and makes its scene available later.
        if (_peer != 3) _adapter.RegisterScene(SceneKey, _scene);
        _ = Task.Run(async () => await Run().AsTask());
    }

    private void BuildView()
    {
        AddChild(new Camera3D { Position = new Vector3(0, 5.5f, 12), RotationDegrees = new Vector3(-22, 0, 0), Current = true });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -30, 0), LightEnergy = 1.4f });
        var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(14, 9) }, Position = new Vector3(0, -1, 0) };
        floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.10f, 0.16f) }; AddChild(floor);
        var overlay = new CanvasLayer(); AddChild(overlay);
        var panel = new VBoxContainer { Position = new Vector2(28, 22), Size = new Vector2(940, 150) }; overlay.AddChild(panel);
        var heading = new Label { Text = "DYNAMIC NETWORK OBJECTS" }; heading.AddThemeFontSizeOverride("font_size", 30); panel.AddChild(heading);
        panel.AddChild(new Label { Text = $"{_role.ToUpperInvariant()} · peer {_peer}   |   native BITKit TCP + UDP   |   actual PackedScene / Node3D" });
        _status = new Label(); _status.AddThemeFontSizeOverride("font_size", 19); panel.AddChild(_status);
        panel.AddChild(new Label { Text = "Left: dynamic prefab · Right: scene object\nSpawn → initial components → late join → owner transfer → despawn" });
    }

    public override void _Process(double delta)
    {
        _main.Pump();
        _status.Text = _phase;
    }

    private async UniTask Run()
    {
        try
        {
            _services = new ServiceCollection().AddNetRpcRuntime(_role == "host", Scope).BuildServiceProvider();
            _runtime = _services.GetRequiredService<RpcContextService>();
            _entities = _services.GetRequiredService<IEntitiesService>();
            _runtime.Faulted += _errors.Enqueue;
            _objects = new NetworkObjectService(_runtime, _adapter, _entities, worldGeneration: 7, localPeerId: _peer);
            _objects.Faulted += _errors.Enqueue;
            _objects.Initializing += Initialize;
            _objects.Spawned += Spawned;
            _objects.Despawning += _ => { _main.AssertMainThread(); _despawning++; };
            if (_role == "host") await RunHost(); else await RunClient();
            await CheckFinal();
            _proof.Passed = true;
            _phase = "PASS · objects removed, scene retained, identity registry empty";
        }
        catch (Exception error)
        {
            _proof.Error = error.ToString();
            _phase = "FAILED · " + error.GetBaseException().Message;
            GD.PrintErr(_proof.Error);
        }
        finally
        {
            _proof.Spawned = _spawned; _proof.Despawning = _despawning;
            _proof.InstantiatedPackedScenes = _adapter.Instantiated;
            _proof.Released = _adapter.Released;
            _proof.MissingSceneAttempts = _adapter.MissingSceneAttempts;
            _proof.MainThreadCalls = _main.Calls; _proof.MarshalledCalls = _main.MarshalledCalls;
            lock (_spawnEvidence) _proof.InitialSnapshots = _spawnEvidence.ToArray();
            try { await Close(); }
            catch (Exception error) { _proof.Passed = false; _proof.Error += "\nClose: " + error; }
            WriteJson($"{_role}-{_peer}.json", _proof);
            GD.Print("NETWORK_OBJECTS_RESULT " + JsonSerializer.Serialize(_proof));
            _testFinished = true;
            await _main.InvokeAsync(() => { GetTree().Quit(_proof.Passed ? 0 : 1); return true; });
        }
    }

    private UniTask Initialize(NetworkObjectHandle handle, CancellationToken token)
    {
        _main.AssertMainThread();
        var node = (ReplicatedCrate)handle.Instance;
        node.Health.Changed += (_, _) => { _main.AssertMainThread(); _proof.MainThreadComponentChanges++; };
        if (handle.IsAuthority) node.Health.Value = handle.IsSceneObject ? 37 : 73;
        return UniTask.CompletedTask;
    }

    private void Spawned(NetworkObjectHandle handle)
    {
        _main.AssertMainThread();
        var node = (ReplicatedCrate)handle.Instance;
        int expected = handle.IsSceneObject ? 37 : 73;
        Check(node.Health.Value == expected, $"Spawned callback saw HP {node.Health.Value}, expected initial snapshot {expected}.");
        Check(node.EntityId == handle.EntityId && node.GetParent() == this, "Actual Node identity/parent was not bound before Spawned.");
        Check(node.Position.IsEqualApprox(new Vector3(handle.IsSceneObject ? 2 : -2, 0, 0)), "Spawn transform did not reach the engine Node.");
        Check(node.Visible, "Spawned node is not active.");
        node.SpawnedWithSnapshot = true;
        if (!handle.IsSceneObject) _dynamic = node;
        lock (_spawnEvidence) _spawnEvidence.Add(new SpawnEvidence { EntityId = handle.EntityId, SceneObject = handle.IsSceneObject,
            HealthAtSpawned = node.Health.Value, RevisionAtSpawned = node.Health.Revision, OwnerAtSpawned = handle.OwnerPeerId,
            GodotMainThread = _main.IsMainThread, NodeInstanceId = node.GetInstanceId() });
        Interlocked.Increment(ref _spawned);
    }

    private async UniTask RunHost()
    {
        _listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, _port));
        _accept = Accept();
        WriteJson("host-ready.json", new { port = _listener.EndPoint.Port, pid = Environment.ProcessId });
        _phase = "Waiting for first client";
        await Until(() => File.Exists(FileName("client-2-connected.json")));
        if (_phaseSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_phaseSeconds), _lifetime.Token);
        var prefab = await _objects.SpawnAsync(Template(false), _lifetime.Token);
        var scene = await _objects.SpawnAsync(Template(true), _lifetime.Token);
        _proof.DynamicEntityId = prefab.EntityId; _proof.SceneEntityId = scene.EntityId;
        Check(prefab.OwnerPeerId == 2 && scene.OwnerPeerId == 1, "Host ownership initialization failed.");
        _phase = "Spawned prefab + scene object; waiting for both actual clients";
        WriteJson("objects-spawned.json", new { prefab = prefab.EntityId, scene = scene.EntityId });
        await Until(() => File.Exists(FileName("client-2-initial.json")) && File.Exists(FileName("client-3-initial.json")));
        if (_phaseSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_phaseSeconds), _lifetime.Token);
        _proof.LateJoin = true;
        // Initial HP was delivered by the reliable object gate, with periodic component publishing OFF.
        _proof.InitialSnapshotsBeforePeriodicSync = true;
        _runtime.StartSynchronization(new NetRpcOptions { SyncInterval = TimeSpan.FromMilliseconds(35), SnapshotInterval = TimeSpan.FromMilliseconds(150) });
        await _main.InvokeAsync(() => { _objects.SetOwner(prefab.EntityId, 3); return true; }, _lifetime.Token);
        await _main.InvokeAsync(() =>
        {
            ((ReplicatedCrate)prefab.Instance).Health.Value = 41;
            ((ReplicatedCrate)scene.Instance).Health.Value = 13; return true;
        }, _lifetime.Token);
        _phase = "Owner transferred 2 → 3; component HP changed to 41 / 13";
        await Until(() => File.Exists(FileName("client-2-updated.json")) && File.Exists(FileName("client-3-updated.json")));
        _proof.OwnershipTransferred = true; _proof.ComponentsUpdated = true;
        if (_holdSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_holdSeconds), _lifetime.Token);
        var despawnPrefab = await _main.InvokeAsync(() => _objects.DespawnAsync(prefab.EntityId, _lifetime.Token), _lifetime.Token);
        await despawnPrefab;
        var despawnScene = await _main.InvokeAsync(() => _objects.DespawnAsync(scene.EntityId, _lifetime.Token), _lifetime.Token);
        await despawnScene;
        WriteJson("host-despawned.json", new { count = _objects.Objects.Count });
        _phase = "Despawned; waiting for client registries and Nodes to clear";
        await Until(() => File.Exists(FileName("client-2-cleared.json")) && File.Exists(FileName("client-3-cleared.json")));
        if (_phaseSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_phaseSeconds), _lifetime.Token);
    }

    private async UniTask RunClient()
    {
        var socket = await TcpTransport.ConnectAsync("127.0.0.1", _port, cancellationToken: _lifetime.Token);
        lock (_connections) _connections.Add(socket);
        _runtime.AttachPeer(1, Wrap(socket));
        // One startup roster read. Later creates, owner changes and removes must arrive unsolicited.
        await _objects.SynchronizeAsync(_lifetime.Token);
        WriteJson($"client-{_peer}-connected.json", new { pid = Environment.ProcessId });
        _phase = _peer == 3 ? "Late join: scene registry deliberately not ready yet" : "Connected; waiting for Host-created objects";
        if (_peer == 3)
        {
            await Until(() => _objects.Objects.Any(o => !o.IsSceneObject));
            Check(!_objects.Objects.Any(o => o.IsSceneObject) && _adapter.MissingSceneAttempts > 0,
                "Missing scene was not deferred until available.");
            await _main.InvokeAsync(() => { _adapter.RegisterScene(SceneKey, _scene); return true; }, _lifetime.Token);
            await _objects.RetryPendingAsync(_lifetime.Token);
            _proof.DeferredSceneRecovered = true; _proof.LateJoin = true;
        }
        await Until(() => _objects.Objects.Count == 2 && Volatile.Read(ref _spawned) == 2);
        var prefab = _objects.Objects.Single(o => !o.IsSceneObject);
        var scene = _objects.Objects.Single(o => o.IsSceneObject);
        _proof.DynamicEntityId = prefab.EntityId; _proof.SceneEntityId = scene.EntityId;
        Check(prefab.IsOwner(2) && !prefab.IsOwner(3) && _objects.Owns(prefab.EntityId) == (_peer == 2), "Initial owner must be client 2.");
        try { ((ReplicatedCrate)prefab.Instance).Health.Value = 999; }
        catch (RpcException error) when (error.Error == RpcError.InvalidRole) { _proof.ClientWriteDenied = true; }
        Check(_proof.ClientWriteDenied, "A client was allowed to mutate authoritative components.");
        WriteJson($"client-{_peer}-initial.json", new { prefab = prefab.EntityId, scene = scene.EntityId, snapshots = _spawned });
        _phase = "Initial component snapshots visible in Spawned; waiting for owner transfer";
        await Until(() => prefab.OwnerPeerId == 3 && ((ReplicatedCrate)prefab.Instance).Health.Value == 41 &&
            ((ReplicatedCrate)scene.Instance).Health.Value == 13);
        Check(prefab.IsOwner(3) && !prefab.IsOwner(2) && _objects.Owns(prefab.EntityId) == (_peer == 3), "Owner query retained stale ownership.");
        _proof.OwnershipTransferred = true; _proof.ComponentsUpdated = true;
        WriteJson($"client-{_peer}-updated.json", new { owner = prefab.OwnerPeerId, health = 41 });
        _phase = "Owner 3 · HP 41 / 13; waiting for Host despawn";
        await Until(() => _objects.Objects.Count == 0 && Volatile.Read(ref _despawning) == 2);
        await CheckFinal();
        WriteJson($"client-{_peer}-cleared.json", new { entities = _entities.Entities.Count, released = _adapter.Released });
        _phase = "DESPAWNED · dynamic Node freed · scene Node retained · registry empty";
        // Keep the transports alive until Host has observed both peer results.
        await Until(() => File.Exists(FileName("host-1.json")));
    }

    private async Task Accept()
    {
        uint nextPeer = 2;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var socket = await _listener!.AcceptAsync(_lifetime.Token);
                lock (_connections) _connections.Add(socket);
                _runtime.AttachPeer(nextPeer++, Wrap(socket));
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }

    private NetworkObjectState Template(bool scene) => new()
    {
        Kind = scene ? NetworkObjectKind.Scene : NetworkObjectKind.Prefab,
        Address = scene ? "" : PrefabAddress, SceneKey = scene ? SceneKey : "",
        OwnerPeerId = scene ? 1u : 2u, PositionX = scene ? 2 : -2,
        RotationW = 1, Active = true
    };

    private async UniTask CheckFinal()
    {
        Check(_objects.Objects.Count == 0 && _entities.Entities.Count == 0, "Despawn left object/entity registrations.");
        await Until(asyncCheck: async () => await _main.InvokeAsync(() =>
            (!GodotObject.IsInstanceValid(_dynamic)) && GodotObject.IsInstanceValid(_scene) && _scene.GetParent() == this && !_scene.Visible,
            _lifetime.Token));
        _proof.DynamicNodeFreed = true; _proof.SceneNodeRetained = true; _proof.RegistryCleared = true;
        Check(_adapter.Released == 2 && _spawned == 2 && _despawning == 2, "Object lifecycle callback count mismatch.");
        if (_errors.TryDequeue(out var error)) throw new Exception("Network/runtime fault", error);
    }

    private async UniTask Until(Func<bool>? check = null, Func<UniTask<bool>>? asyncCheck = null)
    {
        while (!(check?.Invoke() ?? await asyncCheck!()))
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_errors.TryDequeue(out var error)) throw new Exception("Network/runtime fault", error);
            await Task.Delay(30, _lifetime.Token);
        }
    }

    private async UniTask Close()
    {
        // Stop incoming work before releasing engine Nodes; let Godot keep pumping the dispatcher.
        if (_objects != null) await _main.InvokeAsync(() => { _objects.Dispose(); return true; });
        _lifetime.Cancel(); _listener?.Dispose();
        if (_accept != null) await _accept;
        _services?.Dispose();
        lock (_engineTransports) foreach (var transport in _engineTransports) transport.Dispose();
        TcpTransport[] connections; lock (_connections) connections = _connections.ToArray();
        foreach (var socket in connections) await socket.DisposeAsync();
    }

    public override void _ExitTree()
    {
        _lifetime.Cancel(); _listener?.Dispose(); _main.Dispose();
        if (!_testFinished) GD.PrintErr("Network object lab was closed before its test result completed.");
    }

    private string FileName(string name) => Path.Combine(_directory, name);
    private GodotTransport Wrap(TcpTransport socket)
    {
        var transport = new GodotTransport(socket, socket, _main, _errors.Enqueue);
        lock (_engineTransports) _engineTransports.Add(transport);
        return transport;
    }
    private void WriteJson(string name, object value)
    {
        var destination = FileName(name); var temp = destination + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, destination, true);
    }
    private static string Argument(string[] args, string name, string fallback)
    { int i = Array.IndexOf(args, name); return i < 0 ? fallback : args[i + 1]; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public sealed class ObjectProof
{
    public string Role { get; set; } = "";
    public uint Peer { get; set; }
    public int ProcessId { get; set; }
    public string Engine { get; set; } = "";
    public bool RealGodot { get; set; }
    public bool Passed { get; set; }
    public uint DynamicEntityId { get; set; }
    public uint SceneEntityId { get; set; }
    public bool LateJoin { get; set; }
    public bool DeferredSceneRecovered { get; set; }
    public bool ClientWriteDenied { get; set; }
    public bool OwnershipTransferred { get; set; }
    public bool ComponentsUpdated { get; set; }
    public bool DynamicNodeFreed { get; set; }
    public bool SceneNodeRetained { get; set; }
    public bool RegistryCleared { get; set; }
    public bool InitialSnapshotsBeforePeriodicSync { get; set; }
    public int Spawned { get; set; }
    public int Despawning { get; set; }
    public int InstantiatedPackedScenes { get; set; }
    public int Released { get; set; }
    public int MissingSceneAttempts { get; set; }
    public int MainThreadCalls { get; set; }
    public int MarshalledCalls { get; set; }
    public int MainThreadComponentChanges { get; set; }
    public SpawnEvidence[] InitialSnapshots { get; set; } = Array.Empty<SpawnEvidence>();
    public string Error { get; set; } = "";
}

public sealed class SpawnEvidence
{
    public uint EntityId { get; set; }
    public bool SceneObject { get; set; }
    public int HealthAtSpawned { get; set; }
    public long RevisionAtSpawned { get; set; }
    public uint OwnerAtSpawned { get; set; }
    public bool GodotMainThread { get; set; }
    public ulong NodeInstanceId { get; set; }
}
