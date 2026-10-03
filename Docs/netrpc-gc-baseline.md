# Standalone native NetRpc GC/latency baseline

Date: 2026-10-03. Implementation model: **`openai/gpt-6.1-sol`**.

This is a baseline, not a Runtime optimization or a zero-allocation claim. It lives only in the isolated `Net.BITKit.Multiplayer.GCBaseline` worktree, branch `perf/gc-baseline-v1`. Original source snapshot: `Artifacts/AgentBaselines/20261003-084537/manifest.json` in the primary repository. The runner can verify all **316 original file hashes** before testing. No original Runtime, CodeGen, sample, shared solution, or status file is changed.

**Coordinator integration update:** the reviewed original native benchmark is now in the primary tree; 26 cases ran at `Artifacts/NetRpcPerformance/integrated-native-baseline`. Subsequent collection GC changes and fresh primary results are in [the improvement record](netrpc-collection-gc-improvement.md). The historical isolated results below are pre-optimization evidence. A later isolated LiteNetLib-provider extension was canceled and is not integrated/validated in the primary benchmark. Do not use the original snapshot check against primary after intentional integration/optimization; it describes the frozen worktree baseline.

## Scope and build proof

`Tools/NetRpcPerformance` is an independent, engine/UI-free net10 console runner with a netstandard2.1 Contracts project. It uses **`BITKit.Multiplayer.NetRpc`**, MessagePack and real native TCP+UDP sockets; it does **not** use B6/RpcRuntime, TouchSocket, legacy Arena, Godot RPC, or Godot rendering.

The existing `Tools/NetRpc/NetRpc.targets` builds CodeGen net10, invokes `--remote` on `NetRpcPerformance.Contracts.dll`, compiles the resulting native `NetRemote_713615770` proxy/receivers into the runner, and invokes `--netrpc` from the **original intermediate DLL** to the application output DLL on each Build. The shared target is imported unchanged. Core remains netstandard2.1; the nearest compatible native Transport output selected by net10 is net8.0. Both frameworks are recorded in evidence.

`Actor.Notify(int)` is an ordinary class method actually woven at build time. Runtime proof records its generated `__netrpc_recv_2458558790` and moved `__netrpc_body_2458558790`. Guard tests inspect the emitted IL: the public wrapper calls `NetRpcDispatch.Begin`/`FinishVoid`, its receiver directly calls the moved body and uses `NetRpcResults.Void`, and it does not use `FinishTask`. The generated interface has four actual static typed receivers. The void case additionally verifies Host executes exactly warmup+measured calls, Client executes zero local bodies, and the only Client Return frame is the separate application fence. There is no invented void ACK.

## Reproduction (PowerShell, repository root)

```powershell
dotnet build Tools/NetRpcPerformance/NetRpcPerformance.csproj -c Release --nologo
dotnet test Tools/NetRpcPerformance.Tests/NetRpcPerformance.Tests.csproj -c Release --nologo `
  --logger "trx;LogFileName=guards.trx" --results-directory Artifacts/NetRpcPerformance/Validation

# Entire suite: 13 cases per transport, actual independent child processes.
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll `
  --transport both --iterations 500 --warmup 100

# Optional exact original snapshot check; reads but does not write the primary tree.
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll `
  --transport both --iterations 500 --warmup 100 `
  --snapshot-manifest D:/Iris/Documents/GitHub/Net.BITKit.Multiplayer/Artifacts/AgentBaselines/20261003-084537/manifest.json

# Longer concurrent scalar Task workload.
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll `
  --transport both --profile scalar --iterations 2000 --warmup 1000 --concurrency 8

# Parameterized bounded DTO / byte array workloads.
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll `
  --transport both --profile dto --payload-size 1024 --concurrency 4 --pacing-ms 1 `
  --iterations 200 --warmup 100
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll `
  --transport both --profile bytes --payload-size 4096 --concurrency 4 `
  --iterations 200 --warmup 100
```

Options are `--name value` pairs. `--transport direct|relay|both`; `--profile all|idle|void|scalar|dto|bytes|component|syncvar|list|dictionary`; `--iterations` (default 500), `--warmup` (100), `--payload-size` (256, bounded 1..65536), `--concurrency` (1, maximum 256), `--pacing-ms` (0), `--container-size` (128), `--sizes` (`1,128,256` for List/Dictionary cases), `--idle-ms` (250), `--timeout-seconds` (30), `--output` (default timestamped local `Artifacts/NetRpcPerformance` directory), and optional `--snapshot-manifest`. Unknown, duplicate, malformed or invalid options fail instead of silently falling back. Concurrency applies to Task cases; void/state cases have one sequential publisher. Task pacing applies per worker after each result, other pacing after each submission.

The supervisor starts `dotnet runner.dll --role host|client|relay` using `Process.Start`, redirected stdin/stdout/stderr, dynamic loopback listener ports, and owned `Process` handles. It verifies distinct node PIDs, none equal to the supervisor PID. Each case uses fresh processes/providers/scope. Relay is the actual native `RelayEndpoint` in a third process and Host uses the actual native `RelayHostConnection`. Client calls the same native admission API for Direct and Relay. Supervisor waits/control reads are bounded; failures produce case failure JSON including propagated child errors. Only owned children are stopped or, after a five-second shutdown timeout, killed.

## Measurement contract

Every node uses **`GC.GetTotalAllocatedBytes(true)`** (all managed threads), `Stopwatch.GetTimestamp`, and `GC.CollectionCount(0/1/2)` at begin/end. JSON includes the raw begin/end stamps and their deltas, completed operations, callback counts, bytes/frame counts where observable, final values/revisions, percentile samples, PID, OS, machine, architecture, processor count, runtime, GC mode and timer frequency. No asynchronous thread-local allocation accounting, idle subtraction, forced zero or fabricated delivery count is used.

Setup, DI, listener/admission, UDP proof, initial snapshots, RpcMap learning, payload/sample-array creation, warmup and a 100ms drain are **before** allocation windows. Host uses explicit `PublishStateAsync` rather than an automatic state timer, to avoid coalescing measured scalar changes. Idle therefore measures native socket/proof/background and benchmark-control overhead with the same explicit-publication session, not the default automatic synchronization timer or game/UI work.

Host, Client and optional Relay begin sequentially via short stdin commands; work begins only after all acknowledge. Completion is fenced/verified before they end sequentially. **All nodes freeze their end stamps before any report JSON is serialized or printed.** This avoids long JSON/logging during any measured interval. Begin/Run/Wait/End line parsing, async continuation/control allocations, small `OK` replies, Task worker scheduling, the void fence, and completion/drain delays remain in raw measurements. There is no subtraction. Windows are slightly staggered; raw ticks make this visible. The aggregate is a **sum of per-role allocation windows**, not simultaneous application heap size. Supervisor allocations and process startup are not included.

Host/Client counting decorators record successful completed `Send`/`SendFast` calls and received application frame bytes. Bytes include the 25-byte NetRpc header and MessagePack length prefixes, but exclude TCP length framing, UDP authentication/proof datagrams, Relay's six-byte envelopes, socket/IP headers and retransmission. They are not packet-capture bandwidth. Native RelayEndpoint does not expose its internal link counters; its byte/frame fields are explicitly **null**, never zero, while its entire all-thread allocation/time window is included.

Native RelayHostConnection also has no public decorator hook. During **setup only**, this fixture reads its `_peers` dictionary once, requires exactly one admitted client, and uses the Runtime's **public** DetachPeer/AttachPeer API to wrap that existing native peer transport in the same counting decorator as Direct. No private field is written or native protocol replaced. No benchmark RPC is pending at this boundary. It fails explicitly if the sidecar's shape changes, and does not support sidecar reconnect during a case. This benchmark-only introspection is a limitation, not a proposed application integration API.

`IBenchmarkTransportProvider`, `IBenchmarkListener` and `IBenchmarkConnection` are transport-independent entrypoint interfaces owned by the benchmark. The native provider implements them; future isolated Direct adapters can use the same workload and measurement contract. No LiteNetLib project/dependency is required, and no LiteNetLib result is claimed.

## Cases and completion semantics

| Profile | Measured work | Completion / latency |
| --- | --- | --- |
| idle | 250ms wait, no business operation | Baseline allocations/time; no per-operation allocation denominator |
| void | warmed ordinary woven `Actor.Notify(1)` over reliable TCP | Exactly N Host bodies; separate generated `Fence()` Task after submissions; one Return only. p50/p95 are **enqueue** latency, not RTT; elapsed includes fence |
| scalar | generated interface `Task<int> Scalar(int)` request/result | Each result equals its input; exactly N requests/returns and Host bodies. Client p50/p95 are result RTT |
| dto | generated `Task<int> Dto(BoundedDto)` with sequence + bounded prebuilt byte payload | Host validates payload edge bytes; result validates sequence/length. Client result RTT; receiver DTO/array allocations included |
| bytes | generated `Task<int> Bytes(byte[])` | Host validates edge bytes; result validates byte count. Client result RTT; receiver allocation included |
| component | Host-authoritative ECS `NetComponent<int>` changes + explicit publish, real UDP | Client setter rejection tested during setup; Client callbacks/revisions strictly increase; final value/revision match Host; UDP sends/received frames/applied callbacks reported separately |
| syncvar | Host changes interface getter-only scalar + explicit publish | New NetRpc's default interface scalar state, **not** legacy `[SyncVar]` weaving. Exactly N reliable Client callbacks, increasing scalar values, correct final state |
| list | Set index 0 of a prefilled NetworkList | Fixed **element count** 1/128/256, no growth. Exactly N Client Changed callbacks; monotonic revision/final value |
| dictionary | Set key 0 of a prefilled NetworkDictionary | Fixed **element count** 1/128/256, no growth. Exactly N Client Changed callbacks; monotonic revision/final value |

“Fixed capacity” here means fixed logical container count, not a claim that NetRpc's underlying collections never allocate. Their receive-side copy/commit behavior is deliberately included in this baseline. Each measured Set assigns a distinct value, so no no-op is counted as work. No internal storage capacity API is added.

For component final-state repair the Host waits 25ms and performs **one forced full snapshot** after the N changes. Its extra UDP submission is counted (N+1); forced publication also sends the scalar/two collection snapshots via TCP, included in the same raw allocation/byte window. The Client then waits for final state and drains 50ms. This is explicit existing-runtime state repair, not retry of side-effect RPC. Timeouts fail instead of assuming delivery.

`ComponentFrameDeliveryRatio = received Component frames / completed UDP submissions`. `ComponentAppliedChangeRatio = committed Changed callbacks / completed UDP submissions`. A duplicate final repair frame can be delivered without an additional callback. Neither count is inferred from N sends; callback count may be less than N under UDP loss/coalescing, but must be positive, monotonic and end at the correct Host value/revision. UDP is **not** made reliable by the benchmark.

Host p50/p95 for component/scalar-state are mutation+awaited publish submission, not end-to-end Client observation latency. List/Dictionary samples are mutation/submission time (the Runtime owns asynchronous completion). JSON's `LatencyMeaning`/`LatencySamples` disambiguate these from Task RTT. Unsampled Client state latency is zero with **zero samples**, not “instantaneous delivery.” Rates divide actual completed operations by the relevant full process window, including fences/control/drain; do not equate Host publishing rate with Client delivery rate.

## Evidence and observed smoke results

All 26 Direct/Relay cases passed with 100 warmup, 500 measured operations, 256-byte DTO/array payloads, concurrency 1, no pacing, fixed counts 1/128/256. Twelve independent guard tests passed (configuration rejection, percentile logic, actual generated/woven IL, completed-send counter and payload-loan semantics). The manifest verification found **316/316 unchanged original hashes**. Actual smoke environment: Windows 10.0.22631, X64, machine NUKE-X15, 16 logical processors, .NET 10.0.11, workstation GC, Stopwatch frequency 10,000,000.

The initial reviewed full suite is retained at `Artifacts/NetRpcPerformance/20261003-090724-439/`; final guarded rerun is `Artifacts/NetRpcPerformance/baseline-final-20261003/`. Raw per-process `*-host.json`, `*-client.json`, `*-relay.json`, per-case `*-aggregate.json`, `suite.json`, `build-proof.json` and `source-proof.json` preserve measurements/proof. Guard-test evidence is `Artifacts/NetRpcPerformance/Validation/guards.trx`. Earlier runs are preserved, including explicit failure artifacts from initial fixture setup debugging; only fully passing suites are evidence.

Final guarded smoke data from `baseline-final-20261003` (bytes are **all-thread allocation-window totals**, not wire payload bytes):

| Case | Direct Host+Client B | Relay Host+Client+Relay B | Direct / Relay Client RTT p95 ms |
| --- | ---: | ---: | ---: |
| idle | 1,168 | 1,344 | not sampled |
| woven void | 159,864 | 231,840 | not RTT (enqueue only) |
| scalar Task | 1,130,096 | 1,397,272 | 0.174 / 0.248 |
| DTO 256 B | 1,299,656 | 1,557,152 | 0.230 / 0.289 |
| byte[] 256 B | 1,282,168 | 1,553,112 | 0.199 / 0.282 |
| ECS Component | 454,544 | 696,856 | Host publish only |
| scalar state | 334,480 | 374,352 | Host publish only |
| List Set, 1 / 128 / 256 | 222,896 / 473,944 / 727,688 | 201,864 / 473,424 / 737,032 | Host mutation only |
| Dictionary Set, 1 / 128 / 256 | 295,600 / 1,561,856 / 3,183,248 | 314,800 / 1,602,984 / 3,181,584 | Host mutation only |

Both component runs independently observed 501 submitted UDP frames, 501 received Component frames, and 500 committed changes; frame delivery ratio 1.0 versus applied-change ratio 500/501 (~0.9980), final value/revision 600. These are measurements on this loopback run, **not** an assertion that UDP always delivers all sends. Reliable state cases independently observed exactly 500 callbacks each. Zero collection-count deltas in short windows mean no measured GC cycle, not zero allocated bytes.

Direct scalar Task RTT p50/p95 was 0.088/0.174ms, with 500 completed operations in the 0.051634s Client window (~9,684 completions/s). The Direct Host window allocated 308,552 B; Client 821,544 B; aggregate 2,260.192 B/completed operation. These raw numbers are not a zero-GC result.

Additional concurrent/payload smoke evidence is retained in `concurrency8-scalar-final`, `paced-dto1024-final` and `bytes4096-final` under the same local artifact root. Each passed both Direct and Relay: concurrency-8 scalar 2,000 completions, concurrency-4 paced 1,024-byte DTO 200 completions, and concurrency-4 4,096-byte array 200 completions. Scalar p95 was 0.416/0.593ms; DTO 0.993/1.348ms; array 0.272/0.839ms (Direct/Relay). These parameter runs may overlap other benchmark processes on the machine and are functionality/parameter evidence, not controlled comparative performance rankings.

## Limitations / next use

- These are short Windows .NET loopback baselines, with scheduling, pool/thread warmup, tiered compilation and run-to-run variation. One run is not a statistically stable budget; increase warmup/iterations and repeat quiet-machine runs before optimizing.
- No Unity Mono/IL2CPP, Godot UI, AOT/Player, Linux, real WAN/NAT, packet-loss injection, multi-client fanout, reconnect/lifecycle soak, or LiteNetLib baseline is claimed. The component repair is one counted attempt with bounded timeout, not guaranteed delivery under arbitrary loss.
- Per-operation latency samples are preallocated, but native/runtime/Task/collection allocations are intentionally retained. DTO sender creation is setup; receiver decoding remains measured. There is no attempt to hide allocations through current-thread accounting or baseline subtraction.
- Rebuild before every comparison, preserve `build-proof.json` and original snapshot verification, and compare identical payload/count/concurrency/pacing/warmup and the same transport/framework. The standalone projects are intentionally not added to the shared solution.
