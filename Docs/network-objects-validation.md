# Network objects extraction validation — 2026-10-06

## Source and scope

- Core baseline: `37d817a` plus the pre-existing, uncommitted lifecycle/GC cache/benchmark work
- Unity extension baseline: `dcb7b513`, modified locally without commits or pushes
- New functionality is extracted from the existing Unity object service, not a second engine-specific network algorithm
- No prediction, interpolation, interest management, business authorization policy or transport replacement was added

## Verified .NET result

SDK 10.0.401; Core target `netstandard2.1`. Full Release solution build succeeded with one pre-existing unused-event warning. Full solution tests: **236 passed, 0 failed, 1 pre-existing skipped**:

- Core/runtime: 184 passed, 1 skipped
- LiteNetLib: 15 passed
- Native transport: 16 passed
- Benchmark harness: 21 passed

The 29 new tests comprise 19 object lifecycle tests and 10 component-ready tests. Coverage includes real TCP/UDP and generated typed snapshot RPC, authority rejection and forged Client roster frames, owner changes while loading, initial UDP before registration, ready-time values, incomplete/schema/identity/scope manifests, cancellation, late completion, failed-load retry, deferred scenes, duplicate/out-of-order packets, removal tombstones, fresh Client reconnect, new-world scope rejection, missing Client entity wiring, registration baseline reset, engine-thread restoration after gate contention, stale scene cleanup and reentrant disposal during initial component callbacks.

Two intermediate test issues are preserved in evidence: a cancellation test assumed uncooperative asset cleanup completed before the cancelled await returned; a scheduler test joined its own continuation thread. The assertions/synchronization were corrected, not the asynchronous cancellation semantics. The final full suite passed after those fixes and all final source changes.

## Network benchmark regression smoke

The existing actual generated/woven network benchmark ran a separate Host and two Clients, native Direct TCP/UDP, all 11 profiles, 10 warmup and 50 measured operations. **11/11 passed**, with 100 aggregate callbacks for each replicated-state profile. This short unpaced run checks regressions and correctness; its allocation numbers are not a controlled before/after performance conclusion, and object lifecycle is not claimed zero-GC.

## Reproduce

From the Core repository with .NET 10 SDK and .NET 8 runtime installed:

```sh
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo -m:1
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --no-restore --nologo -m:1
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --no-build --no-restore --nologo -m:1 --filter 'FullyQualifiedName~NetworkObject'
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --clients 2 --profile all --iterations 50 --warmup 10 --rate 0 --sizes 1 --output /path/to/new-benchmark-results
```

These tests require local TCP/UDP sockets. In the cloud sandbox, MSBuild process sockets and native test sockets required the supported execution permission route; no virtual display or Unix-socket workaround was used.

## Engine validation and limits

`Samples/NetworkObjectsGodot` contains the real Godot adapter, project, harness and its exact procedure. The topology is a dedicated Godot Host plus two actual Godot 4.7.2 .NET Clients. Files only orchestrate barriers; object and component data travel through the source Package's native network stack. Headless and graphical proof are recorded separately in the deliverable's Evidence directory.

Final headless run: **3/3 actual engine processes passed and exited 0**. Each had two Spawned callbacks and two removals/releases. Clients observed HP 73/37 at Spawned before Host periodic synchronization began; only one initial roster read was used, so later spawn/owner/despawn broadcasts could not be masked by polling. Ownership 2→3 and HP 41/13 propagated; Clients rejected local component writes; late Client 3 recovered its initially unavailable scene. All lifecycle/component callbacks asserted the actual Godot main thread. Dynamic Nodes were freed, authored scene Nodes retained, and object/entity registries cleared. Build log, binary hashes and peer reports are under `Evidence/godot/final` in the archive.

Unity extension static source/contract audit and `git diff --check` passed. No Unity Editor was installed, and no synthetic UnityEngine stubs were used as a compilation substitute. Unity compilation, Player/IL2CPP, domain reload and the disposed-dispatcher Unity SynchronizationContext fallback still require actual Unity acceptance. Matched Core/Unity deployment and source-compatibility details are in the Unity repository's `Docs/network-object-core-migration.md`.

## Evidence and continuation

The delivery archive includes both source repositories, final build/test logs, benchmark JSON/logs, Godot process reports, a manifest with source hashes, the preserved pre-task patch and an object-refactor-only diff reconstructed against that pre-task tree. Generated binaries/caches are excluded. Earlier GC work remains in the Core source and is separated from this task's changes.

Next engine-specific acceptance is Unity Editor import/compile plus one actual Host/two-Client object run, then Player/IL2CPP. Do not infer those outcomes from .NET or Godot evidence.
