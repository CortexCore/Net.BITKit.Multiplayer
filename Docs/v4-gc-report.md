# V4 Unreliable RPC: GC and packet-lease report

**Reliable-codec follow-up:** reliable game messages now also use binary + MemoryPack. The later measurements and remaining allocation limits are in [reliable-binary-validation.md](reliable-binary-validation.md). References to reliable JSON below describe the historical V4 checkpoint, not the current Runtime.

**Native transport follow-up:** packet I/O now defaults to a DI-selected native UDP implementation. Current A/B and ownership validation are in [transport-validation.md](transport-validation.md); the measurements below remain the earlier V4 checkpoint.

**结论：已验证受测路径中的缓冲可以归还、并发积压有硬上限；整个 Arena 仍不是低分配或零 GC 程序。** 下文分别记录纯 RPC 测量、真实 UDP 缓冲归还和整个游戏进程 GC。`ArrayPool` 归还意味着可复用，不保证立即把内存还给操作系统；短时测试也不等于长期无泄漏证明。

**后续调用栈归因已完成：**见 [性能/GC 归因报告](performance-gc-attribution.md)。实际 Client 的主要采样来源是 TouchSocket UDP 每轮新建 64 KiB 接收数组（约 74–76%），不能把下面全进程数字主要归因于 Arena 显示层。该后续报告还补充了真正 GC 暂停与其他运行时暂停的区分；本页保留原始回收/分配基线。

**Recorded:** 2026-09-27 10:31:36 UTC, .NET 10.0.11, Release/net10 test runner, netstandard2.1/C#9 core. This repository currently has an **unborn HEAD** (the workspace files are untracked), so there is no commit hash. Runtime assembly MVID: `dddb27e0-1d00-45c0-a606-7b6e002124b0`. Raw per-wave evidence: [`Artifacts/GcRuns/v4-unreliable-memorypack-20260927-103136.json`](../Artifacts/GcRuns/v4-unreliable-memorypack-20260927-103136.json) (locally generated diagnostic artifact, not a committed fixture).

## Reproduce

From the repository root, in an otherwise idle process (PowerShell):

```powershell
$env:BITKIT_GC_REPORT='1'
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Gc_report --logger 'console;verbosity=detailed'
Remove-Item Env:BITKIT_GC_REPORT
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --no-restore
```

The opt-in test writes a timestamped JSON file under `Artifacts/GcRuns/`. Run `dotnet restore Tests/BITKit.Multiplayer.Tests.csproj` first on a clean machine. No production code forces GC. The test warms the *actually woven* fixture, MemoryPack formatters, reflection and ArrayPool for **320 sends per case**, then runs **three waves of 1,500 sends** per case. A full GC before and after each wave measures retained memory; collection counters and allocation counters are captured **during the wave, before the post-wave forced collection**. The runtime's private pool is wrapped by an instrumented pool solely in tests. `GC.GetAllocatedBytesForCurrentThread()` is used only because this fake wire completes each send and receiver callback synchronously on the test thread. `GC.GetTotalAllocatedBytes(precise: true)` supplements it and includes runner/background allocation; it should be preferred over the thread counter for genuinely asynchronous workloads.

**What is included:** reflection `MethodInfo.Invoke` in the harness, woven `object[]` boxing, host local All body, MemoryPack encoding, routing, pooled packet rental, and (except in sender-only) borrowed-buffer decode and receiver execution. Sender-only still executes the local Host body but intentionally suppresses remote callback dispatch. No Unity, real UDP, presentation/UI, or network-stack allocations are included. These are allocation observations, **not** zero-allocation or cross-library benchmark claims.

## Observations (per host send; wave 1, identical steady allocation in waves 1–2)

| Scenario | Measured application payload | Sender-thread allocated | Whole-process allocated | Gen0/1/2 during each wave | Post-full-GC retained change, waves 1–2 |
| --- | ---: | ---: | ---: | --- | ---: |
| One `ArraySegment<int>` element, receiver suppressed | 120 B | 1,298 B | 1,300 B | 0 / 0 / 0 | 0 B |
| One `ArraySegment<int>` element, one receiver | 120 B | 2,250 B | 2,252 B | 0 / 0 / 0 | 0 B |
| 30 unmanaged five-scalar poses, one receiver | 768 B | 2,946 B | 2,948 B | 0 / 0 / 0 | 0 B |
| Same 30 poses, two receivers | 768 B **once**, shared | 4,698 B | 4,700 B | 0 / 0 / 0 | 0 B |
| Same 30 poses, eight receivers | 768 B **once**, shared | 14,778 B | 14,780 B | 1 / 0 / 0 | 0 B |

The single-send pose test with different scope/values measured **764 B binary vs 2,384 B** for its previous JSON prototype. Size depends on scope/method identity and values; the measured 768 B pose batch leaves 132 B below this fake wire's 900 B payload limit. The first wave's retained-memory deltas varied from −48 to −840 B; waves 1–2 had zero change within each stable, live test setup. The presence of a Gen0 collection in *each* eight-peer wave shows normal managed allocation/collection; no Gen1/2 collection occurred during measured waves. Full GCs done by the diagnostic itself are deliberately excluded from those counters. Zero collections in a short 1,500-send wave is **not** evidence that a sustained application has zero GC.

## Ownership, bounds, and recovery

- Every completed wave had **0 active packet leases, 0 outstanding instrumented rentals/capacity, 0 pending reliable replies, and exactly matched rents/returns**. Under the synchronous test wire, peak was one active packet rental of 1,024 B per runtime, including eight-recipient fanout. The process-global `ArrayPool<byte>.Shared` may retain returned arrays for reuse; *returned capacity is not an active leak*, and `GC.GetTotalMemory` may include pool caches.
- Separately, `Delayed_wire_cancellation_and_real_128_send_cap_release_all_rentals` holds **128 actual sends** concurrently and observes 128 active leases. The 129th is rejected with `LimitExceeded`, without a fallback or new send. Completing/canceling the held wire tasks returns all rentals and pending replies stay zero. The shared pool's concrete bucket size can vary by runtime; the sample 900 B payload uses a 1,024 B rental, approximately 128 KiB of live packet capacity at that cap. A different wire payload limit/bucket size changes that capacity. A call being *measured* can temporarily rent up to the 64 KiB candidate ceiling, but over-limit sends are returned before they enter the in-flight table.
- Raw JSON `lifecycle`: a faulted send had 0 active leases; a held send had 1, **remained 1 after runtime disposal while the wire still owned its borrowed memory**, and returned to 0 after that wire task was canceled. The instrumented pool recorded 2 rents/2 returns. Returning it immediately at disposal would risk a use-after-return; if a transport never completes its task, up to the 128-slot limit may stay retained indefinitely. The transport must complete/fail its outstanding sends on shutdown. This report does **not** claim recovery from a noncooperative wire.
- The application datagram codec is version **2**, incompatible with the former JSON prototype. It encodes synchronously into an `ArrayPool`-backed `IBufferWriter<byte>` and returns that rental after **all** fanout send tasks complete/fail. `MeasureUnreliableCall` uses the same codec and always returns its rental. V4 rejects a delivered payload beyond the wire's advertised maximum (900 B in the fake wire), caps measurement candidates at 64 KiB, RPC parameters at 32, top-level array/segment counts at 256, and outstanding sends at 128 **per runtime**. Host relay also owns a copied bounded frame before its borrowed callback returns.

## Untrusted decode scope

The wire uses full, registered method identity and fixed argument types, not attacker-supplied CLR type names. Unreliable weaving accepts scalar/unmanaged value types, top-level strings and one-dimensional arrays/`ArraySegment<T>` of supported elements, plus flat, annotated `[MemoryPackable]` reference DTOs containing only supported scalar/unmanaged fields/properties. **Nested reference DTOs, reference-bearing struct DTOs, nested collections, and nested strings are rejected at weave time** rather than parsed with a bespoke generic MemoryPack walker. Top-level arrays/segments are checked against 256 elements before MemoryPack allocates; top-level string lengths are checked against the bounded argument slice using overflow-safe arithmetic. Tests inject a forged `int.MaxValue` top-level collection count into a sub-900 B packet and observe rejection without invoking the RPC body. A generated/custom MemoryPack formatter or callback is still application code: do not treat unknown third-party formatter behavior, arbitrary DTO constructors, or all possible malformed nested schemas as formally proven memory-safe by this report. Reliable JSON/SyncVar behavior is unchanged.

## Real UDP ownership and backpressure

`Tests/DatagramTests/UdpSocketTests.cs` supplements the codec-only results with real TouchSocket sockets and internal rental counters:

- `DirectRealUdpCyclesAndEndpointRotationReturnAllLaneRentals`: two sets of 35 real datagrams with endpoint rotation, then leave/dispose. Data stays correct after the caller reuses its array. Both old/new client lanes and the Host end with **rents = returns, 0 outstanding rentals/bytes, 0 in-flight sends**.
- `RelayRealUdpRebindAndShutdownReturnForwardedReceiveRentals`: real relay forwarding, Client and reverse-Host endpoint rotation, then shutdown. All old/new lanes and the Relay return their rentals, including the Relay's owned copy of borrowed receive memory; forwarding in-flight count returns to 0.
- `StalledUdpSendsCapAt64AndReturnAllPooledBuffersAfterFault`: submits 200 sends against an intentionally stalled writer. Exactly **64 sends / 128 body-and-wire rentals** remain in flight; at least 136 sends are dropped rather than queued. Faulting the writer returns all rentals. This stall is injected, not a claim that loopback reproduces Internet congestion.
- `DelayedAndFaultedUdpSendKeepsBorrowedWireRentalUntilTaskFinishes`: two rentals remain valid while the write is pending, then return on fault; a subsequent real UDP send succeeds.

Control sends are separately capped at **4 per grant**. Relay forwarding reserves one of **64 global slots before renting/copying** its borrowed payload; that rental remains owned through asynchronous completion. Relay defaults allow at most 128 rooms and 64 Clients per room. Current small packets normally occupy 1–2 KiB pool buckets, but this is an observation of the tested pool, not a public bucket-size guarantee. These counters exclude HMAC/UTF8/reflection allocations, caller arrays, and TouchSocket's own internal buffers. A permanently non-completing external write still prevents safe early return; finite backpressure limits retention but does not cancel arbitrary third-party code.

## Whole-process Arena GC (real network, no forced collection)

The App starts sampling after ready plus a **3-second warmup**, approximately once per second. It stores only initial, sampled peak-managed-heap, and final snapshots with aggregate counters, not an ever-growing history. `GC.GetTotalAllocatedBytes(precise: true)` covers all process threads; the allocation rate is a window average. `GC.GetTotalMemory(false)` is approximate heap occupancy, **not a post-full-GC live-object census**. Committed/fragmented heap values come from the last GC, and working set includes native/runtime/graphics memory. The final sample is taken before session disposal; clean exit and separate rental tests supply shutdown evidence.

All MB values below are decimal (1 MB = 1,000,000 bytes). Different runs are workload observations, not an apples-to-apples transport benchmark.

### Direct headless soak

Command: `dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --soak-seconds 90`.

Evidence: `Artifacts/ArenaRuns/20260927-104051-4ec8a4/summary.json`. The requested 90-second scenario has staggered join/exit and cleanup time, so process windows differ.

| Game process | Window | Allocated total | Average rate | Gen0 / Gen1 / Gen2 delta | Heap initial / sampled peak / final |
| --- | ---: | ---: | ---: | --- | --- |
| Host | 109.0 s | 213.8 MB | 1.96 MB/s | 17 / 2 / 1 | 7.60 / 14.94 / 14.38 MB |
| Alice | 99.0 s | 338.7 MB | 3.42 MB/s | 27 / 3 / 1 | 1.44 / 14.43 / 3.68 MB |
| Bob | 87.0 s | 294.5 MB | 3.39 MB/s | 23 / 2 / 1 | 2.44 / 14.42 / 10.83 MB |
| Charlie | 91.0 s | 314.2 MB | 3.45 MB/s | 25 / 3 / 1 | 2.77 / 14.63 / 5.73 MB |

This run predates the final Relay receive-copy pooling and diagnostic-file retry changes. It is retained as a dated Direct baseline, not presented as a new measurement after every subsequent change. All game processes exited normally; Host motion arrays were allocated twice total (one player buffer, one bullet buffer), not once per tick. The final sampled heaps are not expected to equal startup: pool/JIT/schema caches and time since the latest GC differ.

### Graphical Relay, pause/resume and rebind

Evidence: `Artifacts/ArenaRuns/20260927-105929-40525a/summary.json`, after Relay receive-copy pooling and bounded diagnostic-file retry. Host and Charlie were headless; Alice/Bob rendered Raylib 2D/3D.

| Game process | Window | Allocated total | Average rate | Gen0 / Gen1 / Gen2 delta | Heap initial / sampled peak / final |
| --- | ---: | ---: | ---: | --- | --- |
| Host | 52.0 s | 98.9 MB | 1.90 MB/s | 8 / 1 / 0 | 2.48 / 14.76 / 2.88 MB |
| Alice | 41.4 s | 148.5 MB | 3.59 MB/s | 12 / 1 / 0 | 2.19 / 14.37 / 5.54 MB |
| Bob | 28.4 s | 95.1 MB | 3.35 MB/s | 8 / 1 / 0 | 8.00 / 13.95 / 5.18 MB |
| Charlie | 33.0 s | 112.8 MB | 3.42 MB/s | 9 / 1 / 0 | 3.84 / 13.74 / 4.50 MB |

The Host recorded **2 motion-buffer allocations**, 907 player batches, 748 bullet batches, and **7,956,360 synchronous position-send allocated bytes**. The latter surrounds the actual `publish(...)` call: it includes encoding/call dispatch on that thread, **excludes the preceding batch-measurement loop**, and omits later asynchronous work; it must not be substituted for the **98.9 MB whole-process** total. Received socket counters reset at rebind, whereas gameplay batch counts continue. Relay room/client/admission/UDP-binding counts ended at zero. The final integrated repeat, `20260927-110510-5566ab`, also passed and contains a fresh full GC report; it strengthens post-rebind evidence by requiring Tick advancement after the completion marker.

The whole-process figures include reliable JSON, full view/report snapshots, interpolation, reflection/boxing, HMAC, TouchSocket, diagnostic serialization and graphics-related managed work. They do not attribute bytes to individual call stacks. **MB/s allocation remains material:** pool ownership is controlled, but a low-GC frame-time budget is not yet established. No GC pause-duration trace, allocation-stack profiler, isolated Relay/Lobby process GC report, hours-long saturation test, or Unity/IL2CPP measurement was collected. Next optimization should use an allocation profile to prioritize these sources rather than assume all remaining bytes belong to MemoryPack.
