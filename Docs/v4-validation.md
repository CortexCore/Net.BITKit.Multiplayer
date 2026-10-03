# V4 integration validation — 2026-09-27

Windows x64, .NET 10 SDK, Release. Independent `Net.BITKit.Multiplayer` repository; no Unity/editor or existing business-server integration was performed. Repository has no commit yet, so evidence is identified by dated local artifacts rather than an invented revision hash.

## Build and tests

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
```

Final complete build: **0 warnings, 0 errors**. Core remains C#9/netstandard2.1; TouchSocket builds netstandard2.1 and net8.0. All **130 tests passed**, none skipped:

| Suite | Passed |
| --- | ---: |
| Runtime / actual Cecil-woven fixtures / GC ownership | 48 |
| Actual UDP sockets / authenticity / rebind / pool lifecycle | 11 |
| Reliable Relay regression | 16 |
| Lobby | 9 |
| Arena Game / deployed codec dependency / GC sampler / diagnostic file recovery | 29 |
| Presentation / interpolation | 17 |

Core coverage includes real woven Unreliable Host/All/Target, interface/concrete semantics, lane-declaration mismatch, bounds, prefix-only segments, generated reference DTO, immutable encoded payload after source-buffer reuse, delayed fanout, faults/cancellation, and the 128-send cap. No reliable reply waiters are created for Unreliable calls. Socket tests exercise forged MAC, replay, fresh out-of-order packets, wrong endpoint, expired/replaced grant, cross-room separation, oversized datagrams, pause/rebind and rental recovery.

## Actual independent-process evidence

Paths below are under `Artifacts/ArenaRuns/` and are locally generated, ignored artifacts. E2E launches independent Lobby, authority Host, Clients, and optional Relay processes. It deliberately occupies the Direct TCP game port during Relay tests and checks for fallback attempts.

| Scenario | Passing run |
| --- | --- |
| Direct UDP, actual Client rebind, late join and shutdown | `20260927-105437-9beb0a` |
| Relay, UDP-only pause, rebind, 2D/3D windows, shutdown | `20260927-110510-5566ab` |
| Kill Relay: readiness/frame cleanup, room withdrawn, no Direct fallback | `20260927-110510-0c7d4c` |
| Kill Host: Relay routes removed, clients disconnected, room withdrawn | `20260927-110510-950068` |
| 90-second Direct headless soak and whole-process GC baseline | `20260927-104051-4ec8a4` |

Final graphical Relay run:

- **3,894 UDP forwards / 1,527,708 application bytes**; largest actual observed UDP datagram **550 B**, below 1,200 B. Binding/keepalive are separate from gameplay counters.
- Paused only Relay UDP: Bob's PositionTick remained **71**, with 30 received datagrams unchanged. Reliable probes advanced **1 → 5** and real Health SyncVar updates **4 → 7**; Host hits continued. Resume advanced PositionTick to **170**. No TCP pose fallback hid the freeze.
- Actual Client rebind completed, retaining Peer/Player/Scope. The last observed Tick at completion was **501**, then **511** with 5 datagrams on the replacement socket. This checks progress after completion, not merely since startup; socket counters reset when the lane is replaced.
- Roster mapping, late join, independent movement, authoritative death/respawn, display membership/Health, process leave, and room shutdown passed.
- Final Relay rooms, clients, pending admissions and UDP-bound peers were all **0**.
- `alice.png` and `bob.png` were read back: both show the actual Relay route, connected/ready state, players/HP and UDP-bound HUD, in 2D and 3D respectively. They are snapshots, not a claim of manual interaction or a frame-time benchmark.

## Integration fixes discovered by validation

1. Stale `--no-restore` application assets omitted the new MemoryPack dependency from deployment and `.deps.json`, producing `FileNotFoundException` during the first motion measurement. Restoring/rebuilding the project graph fixed it; GameTests and E2E now check deployed codec presence explicitly. A later teardown `Unbound object` was a consequence, not the initial cause.
2. UDP callback/owner lock inversion was removed: socket sends and application callbacks execute outside the UDP registry lock. Concurrent data/statistics/rebind regression tests cover the boundary.
3. Relay forwarding now rents its borrowed-buffer copy only after reserving a bounded forwarding slot and returns it after asynchronous send termination.
4. Windows report-file replacement can transiently fail when another process holds a file. App/Relay share a bounded retry writer (six attempts, 25 ms delay per write/replace stage), with lock/recovery/permanent-denial tests. The earlier failed graphical run `20260927-105437-cb4689` reported `UnauthorizedAccessException`; its log did not preserve a full exception stack. Subsequent full tests and graphical runs passed. E2E now reports early node errors rather than hiding them behind a later generic timeout.

## Memory and compatibility

See the **[GC report](v4-gc-report.md)** for warmed codec allocations, live-rental accounting, overloaded send limits, real UDP return tests, and full game-process GC tables. In brief: source pose arrays are reused, packet rentals return on completed/faulted/canceled sends, overload is bounded; total Arena allocation is still approximately 2–3.6 MB/s per measured game process and is not zero GC. No production forced collection is used.

Unreliable parameters use MemoryPack.Core 1.21.4 and application codec **version 2**; all participating peers must upgrade together and use matching schemas. Reliable RPC/SyncVar JSON formats remain unchanged. Bounds and public behavior are documented in [V4 usage](v4-guide.md).

Remaining scope: Internet/NAT/VPS deployment, trusted-domain TLS success on public infrastructure, hours-long churn/load, allocation-stack and GC-pause profiling, Relay/Lobby-only GC attribution, Unity ILPP/UniTask/main-thread integration, AOT/IL2CPP/trimming and PDB preservation. The local socket tests and sampled heap recovery do not establish those results.
