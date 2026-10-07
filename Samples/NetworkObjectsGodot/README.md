# Godot dynamic network objects

This example consumes the shared source Package `NetworkObjectService`. The Core service owns the world roster, IDs, owner changes, revisions, initial component gate and despawn. Godot is only an engine adapter.

The automated topology is **one dedicated headless Godot Host and two real Godot .NET Clients**. The Host is never also a local Client. It uses the source Package native TCP+UDP transport; no Godot multiplayer/RPC API or private copy of the lifecycle service is involved.

## Run

Prerequisites: Godot **4.7.2 .NET**, .NET 8+ SDK, Python 3. Set `GODOT_HOME` to the .NET Godot distribution containing `GodotSharp/Tools/nupkgs`; put `godot` and `dotnet` on PATH. The local NuGet configuration resolves the official bundled Godot SDK.

From this directory:

```sh
bash run-e2e.sh
```

For two windows on an existing real desktop, holding the updated Nodes for observation:

```sh
bash run-e2e.sh --visible --phase-seconds 5 --hold-seconds 5 --client-size 880x560
```

To reuse the built assemblies and choose an evidence directory that does not already exist:

```sh
python3 run-e2e.py --godot /path/to/godot --output /path/to/new-results
```

The harness prints the evidence directory and exit status. `summary.json`, individual peer JSON reports and all engine logs are preserved. It chooses an ephemeral loopback port, starts client 2, waits for its initial state, then starts client 3 late. All processes must exit cleanly. No virtual display setup is performed.

`--phase-seconds` pauses before spawning, before ownership transfer and after despawn, so a real screen recording can show the entire lifecycle. `--hold-seconds` controls the updated-state pause. Use `--client-size 620x440` for a 1280-pixel desktop.

## What is tested

1. Host dynamically instantiates a real `PackedScene` Node3D, assigns its identity/owner and registers its `NetEntity`
2. Host also binds an authored scene Node using a stable scene key
3. `Spawned` on each peer sees HP **73 / 37**, the final identity, active state and transform; Host periodic component publishing stays **off** until both clients confirm these initial snapshots
4. Client 3 joins after client 2 has both objects, initially has no registered scene provider, then loads that provider and uses `RetryPendingAsync`
5. Clients reject direct writes to authoritative components
6. Host transfers prefab ownership **2 → 3** and updates HP to **41 / 13**; both clients observe the new local ownership queries and periodic component state
7. Host despawns both objects; each dynamic Node is actually freed, the authored scene Node is retained/hidden, and object/entity registries are empty
8. Initializing, Spawned, Despawning and every component Changed callback assert the real Godot main thread

Each client performs one initial roster synchronization. Later spawn, ownership and removal updates arrive unsolicited; periodic snapshot polling cannot mask a broken lifecycle update. Files in the evidence directory are orchestration barriers only. IDs, object state, owner changes and component values are carried by the real network protocol.

## Read the code

- `GodotNetworkObjectAdapter.cs`: local prefab address → `PackedScene`, scene key → `Node3D`, transform/active state and instance release
- `IGodotNetworkObject.cs`: engine-local identity/provider boundary; any appropriate Node3D scene root may implement it
- `ReplicatedCrate.cs`: sample identity provider and pure Core `NetComponent<int>`; this contains no lifecycle protocol
- `GodotTransport.cs`: copies borrowed native ingress into a bounded main-thread dispatch queue, so normal UDP component application and Changed callbacks are engine-safe too
- `GodotThread.cs`: engine-thread dispatch, 1,024-operation cap; ingress additionally has an 8 MiB owned-buffer cap
- `ObjectLab.cs`: startup, assertion scenario and small 3D display

`NetworkObjectService` handles its built-in lifecycle messages with native typed descriptors/receivers. The sample does not introduce a separate woven business contract; the existing `Samples/NetRpcGodot` remains the generated/woven business RPC example.

## Adapter lifetime rules

Register local resources and scene Nodes before synchronization, or retry scene lookup after loading. Stable scene keys are application-authored. Prefab addresses resolve only through the explicit local map.

The sample leases own their DI providers. Core owns entity registration/unregistration. A dynamic prefab is freed on release; an authored scene Node stays alive and can be registered again. A stale state cannot apply to a different Node identity, and releasing an old lease cannot clear a newer provider binding. This particular provider conservatively refuses a second lease until its previous provider has been released.

Synchronous Host owner changes and service disposal run on the Godot thread. `INetworkObjectDispatcher` returns async Core lifecycle work to that thread after awaits. Keep pumping the engine while work is pending; do not synchronously wait for a network operation from `_Process` or another engine callback. This sample uses a bounded transport-dispatch boundary because the Core runtime is engine-independent.

## Validation boundary

Headless engine evidence verifies actual engine Nodes and callbacks, not visible rendering. A visible run must be recorded separately. This sample does not claim Godot reconnect, Relay, adverse-network behavior, Unity Editor/Player/IL2CPP, or overall zero-GC validation. Those require their own focused checks. Shared-Core reconnect/stale-generation/race coverage is recorded with the Core test suite.
