# Godot 3D GC investigation: bounded lifecycle fixes

Date: 2026-10-06. Base commit: `37d817a`. No commit or publication performed.

This records lifecycle issues found while profiling the separate `MultiplayerCharacters3D` real Godot host/two-client demo. It is not a core allocation improvement claim or a replacement for that demo's GC report.

## Verified shutdown race and core change

`RpcContextService.Dispose` marks the runtime disposed and cancels synchronization. An already pending `SendMember` fanout can resume afterward and reach `SendFrame.CheckAlive` for the next peer. The synchronization loop previously caught cancellation but allowed its own `ObjectDisposedException` to escape to `Faulted`.

`Src/Runtime/NetRpcState.cs` now ends that internal worker only when the exception names `RpcContextService` and that runtime is actually disposed. This does not change public call behavior, suppress arbitrary business/transport failures, alter socket reliability, or release a pending send's borrowed memory early.

`AutomaticSynchronizationExitsWhenDisposedDuringHeldFanout` deterministically held the first peer send, disposed the host, and resumed the fanout. It failed on the original source with the same `CheckAlive -> SendFrame -> SendMember -> Synchronize` exception as the demo, then passed after the change. It also verifies public publication after disposal still throws. `AutomaticSynchronizationStillReportsBusinessDisposalErrors` verifies a different disposed business resource still raises `Faulted`.

Evidence retained locally: `Artifacts/gc-shutdown-before.log`, `Artifacts/gc-shutdown-fixed.log`, `Artifacts/gc-final-focused.log`, and `Artifacts/TestResults/gc-final-focused.trx`.

## Buffered admission and demo startup ordering

`TcpTransport.OnReceived` can replay buffered frames synchronously during subscription. `AddNetRpc` subscribes inside its runtime singleton factory; a buffered state request can run `EnsureServices` and recursively resolve the runtime before the first factory result is cached. A deterministic buffered-transport fixture observed **two subscriptions**, not one, using that ordering. The demo's immediate port-reuse regression also failed with `RPC object already belongs to another scope`.

The demo host now registers `AddNetRpcRuntime`, resolves the runtime and game, attaches its first peer, then starts synchronization with the same 33 ms / 1 s settings. The client path is unchanged. `HostResolvedBeforeBufferedTransportAttachmentKeepsOneRuntime` verifies exactly one subscription, one state response, and no fault for this supported ordered construction. Original failing evidence: `Artifacts/gc-buffered-startup-before.log`.

This is a demo startup fix and a documented integration constraint. The general `AddNetRpc` factory has not been redesigned to handle arbitrary synchronous replay.

The demo also stops/drains its business timer before disposing the service provider. `CharacterGame.Dispose` is idempotent and takes its tick lock, so DI may safely dispose it again. The core worker change remains necessary because the runtime owns a separate synchronization worker.

## Verification and remaining diagnostic

- Release Core build with its netstandard2.1 runtime succeeded as part of testing
- `NetRpcHotspotTests`, `NetRpcDesignTests`, and `NetRpcUniTaskTests`: **36 passed, 0 failed, 0 skipped** on .NET 10
- Subsequent complete solution Release test command: **196 passed, 0 failed, 1 existing optional allocation benchmark skipped** (Core 153 + LiteNetLib 15 + Transport 16 + performance guards 12). Exit code 0; existing `RoomTransportHub` CS0067 warning remains. `Artifacts/gc-final-suite.log` preserves all project results; its fixed TRX filename was overwritten between projects, so that single TRX is not an aggregate
- The actual demo socket lifecycle passed caller ownership, movement, stop timeout, reconnect, first-peer departure, host shutdown and immediate port reuse; pose parsing passed 207 cases. Evidence in the demo: `Evidence/gc/lifecycle-final.log` and `lifecycle-final-build.log`
- The full .NET solution run is not Unity/Player/AOT verification
- Real Godot functional loopback reached both `CHARACTER_PASS` results but its strict log gate failed when a state send hit `IOException: Broken pipe` during intentional peer departure. The final replay also retains this failed gate; both-viewer skeleton/state assertions separately passed. That transport diagnostic was not suppressed or reclassified. Demo `Evidence/regression/disconnect-broken-pipe.log` and `final-loopback-*` preserve the evidence
- EventPipe attachment in the sandbox failed diagnostic endpoint discovery, producing no `.nettrace`. No allocation-stack conclusion is claimed from that failed capture; the demo's all-thread and main-thread section counters remain separate evidence

The demo's final controlled measurements and report are in `GC-BASELINE.md` and `Evidence/gc/replay-*`. Both variants record actual input message counts and retain the same nominal30 Hz workload; early runs with frame-delta rounding variability remain exploratory evidence, not the headline comparison. No core steady-state allocation gain is claimed from this lifecycle patch.

Remaining constraints: the general AddNetRpc factory's synchronous buffered-replay ordering has not been redesigned, and transport-close diagnostics retain their existing reporting semantics. The strict loopback log gate is not all-green.
