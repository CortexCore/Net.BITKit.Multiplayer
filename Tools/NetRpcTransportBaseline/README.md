# Native transport diagnostic

This small console tool measures only the existing `TcpTransport` byte boundary over real TCP and authenticated UDP. It is a diagnostic companion to `Tools/NetRpcPerformance`, **not a replacement for that full generated/woven NetRpc benchmark**. It has no engine, UI, RPC codec, DI, state synchronization, or business runtime. No production transport changes are needed.

One process owns two Host–Client connections (four native endpoints). Each tick sends once along each of four edges: Client 0 → Host, Client 1 → Host, Host → Client 0, Host → Client 1. A rate of 60 means 60 ticks/s and 240 intended sends/s; 300 means 300 ticks/s and 1,200 intended sends/s. All-thread allocations include both sides and harness overhead; they cannot be attributed separately to Host or Client.

## Run

From the repository root with a .NET 10 SDK:

```sh
dotnet build Tools/NetRpcTransportBaseline/NetRpcTransportBaseline.csproj -c Release --nologo
dotnet Artifacts/bin/NetRpcTransportBaseline/Release/net10.0/NetRpcTransportBaseline.dll --self-test

# Quick functional smoke: 7 cases, 300 measured ticks, one repetition.
dotnet Artifacts/bin/NetRpcTransportBaseline/Release/net10.0/NetRpcTransportBaseline.dll \
  --iterations 300 --warmup 128 --repeats 1 --rates 300 \
  --output Artifacts/NetRpcTransportBaseline/smoke

# 42 cases: 600 ticks, three repeats, 32/256/1024-byte payloads, 60/300 ticks/s.
dotnet Artifacts/bin/NetRpcTransportBaseline/Release/net10.0/NetRpcTransportBaseline.dll \
  --iterations 600 --warmup 256 --repeats 3 --rates 60,300 \
  --sizes 32,256,1024 --output Artifacts/NetRpcTransportBaseline/repeated
```

Use a quiet machine, with no other benchmark running. Each case creates fresh connections. The project intentionally stays outside the shared solution and uses the nearest compatible existing Transport target (`net8.0`); the executable runs on .NET 10. Exact runtime, OS, CPU count, GC mode, assembly target and options are saved in `environment.json` and `suite.json`.

## Measurement and validation

- Preallocate four payload arrays, receive-sequence tables and pacing state. Mutate only the sequence/phase header between sends. Await each native `UniTask` send before reusing its input. No per-operation `AsTask`, logging, timer, callback closure or payload-array construction is added by this harness
- Each receiver synchronously checks exact payload size, edge identity, phase, sequence range and every remaining pattern byte. Per-edge sequence tables count distinct received messages and expose duplicates, invalid data and missing deliveries; counts are never inferred from submitted iterations
- Admission, UDP endpoint proof, real sends/receives during warmup (1,000 ticks/s to avoid an unbounded socket-buffer burst), and the initial quiet drain occur before measurement. Disposal, the final quiet drain and JSON/log generation occur afterward
- `GC.GetTotalAllocatedBytes(true)` and `GC.CollectionCount(0/1/2)` sample every managed thread. Raw start/end values and monotonic timestamps are retained. No forced GC, thread-local accounting, control subtraction or zero-allocation assertion is used
- The measured window includes pacing and a bounded receive-completion fence. Producer and receive-fence time are reported separately. A missing UDP receive triggers the configured timeout, keeps the partial counts, marks the case invalid and causes exit code 1. Missing at deadline is not a claim of permanent packet loss. The excluded final drain reports any late delivery separately
- Transport faults and unexpected closes are counted. Setup/send exceptions are printed and saved as failure artifacts. Each case has an overall cancellation deadline as well as the receive fence; failures are never silently converted into successful traffic
- `control` warms real TCP connections, retains those native sockets, and runs the exact same iteration/input-header mutation/pacer loop with no sends. It runs once per rate and repeat (size is the first requested size). It gauges warmed socket/background plus harness overhead; it is reported separately and never subtracted
- The linked `../NetRpcPerformance/RatePacer.cs` uses absolute-deadline `Thread.Sleep` pacing without per-tick task/timer allocations. OS scheduling may miss deadlines; actual measured throughput is authoritative
- Payload-byte counters count completed application payload sends and observed receive callback bytes, not TCP/IP headers, TCP length prefixes or the UDP token/header. Allocation B/send and B/receive both divide the same combined process allocation by their own actual counts; do not add the two metrics together
- Gen0/1/2 deltas of zero mean no GC collection happened during this short window. They do not mean zero allocations or zero pause cost in a longer run

Supported pair options: `--iterations` (600), `--warmup` (256), `--repeats` (3), `--rates` (60,300), `--sizes` (32,256,1024), `--channels` (control,tcp,udp), `--receive-timeout-ms` (3000), `--drain-ms` (100), `--output` (timestamped artifact directory). Unknown, repeated or invalid options fail. `--self-test` alone runs guards for payload validation, duplicates, missing delivery, sequence ordering, awaited payload loans, send failures, zero-send control and option rejection.

## Scope limits

Loopback TCP/UDP on this runtime is not WAN/NAT behavior, sustained saturation, per-role allocation, full-module cost, Unity/IL2CPP, Godot, or engine memory. The UDP session proof is transport admission, not application account authentication. Repeated cases expose ordinary run-to-run and JIT/threadpool variation; retain outliers rather than treating the minimum as the general cost.

This diagnostic is intentionally small: no new benchmark framework, production abstraction, alternative transport, RPC implementation, or automatic optimization.

## Observed evidence: 2026-10-06

Environment: Debian 13 x64, .NET **10.0.12**, 9 reported logical processors, workstation GC / Interactive latency mode; existing Transport assembly targets net8.0. The diagnostic assembly uses the existing pre-optimization library binaries (base repository HEAD `37d817a99be370518056139dafd590b0ad2af08b` plus the existing lifecycle fix). Later parallel full-module optimization builds are not part of these measurements.

Release build: **0 warnings, 0 errors**. Diagnostic self-tests: **8/8 passed**. Final paced-warmup smoke: **7/7 passed**. Repeated matrix: **42/42 passed** (36 transport cases + 6 worker controls), with 56,160 completed sends and independently validated unique receives. Zero invalid/duplicate/missing/late receives, transport faults, unexpected closes, or Gen0/1/2 collections occurred in the repeated windows. These collection counts do not erase the nonzero allocations below.

The primary group used 600 ticks at 300 ticks/s (approximately 2 seconds, 2,400 actual sends and receives per transport case). The supplementary group used 180 ticks at 60 ticks/s (approximately 3 seconds, 720 actual sends and receives per transport case). Both used 256 warmup ticks, 100 ms excluded drains and three repetitions. Four directed sends per tick means 1,200 and 240 intended sends/s respectively. **Durations/counts intentionally differ between the two rate groups**; compare their normalized rates with the raw windows, not raw allocation totals alone.

All values below are **raw combined-process allocated bytes in repetition order**, with no control subtraction:

| Channel / payload | Primary, 300 ticks/s | Supplementary, 60 ticks/s |
| --- | --- | --- |
| Worker control | 0 / 48 / 96 | 0 / 48 / 0 |
| TCP, 32 B | 56 / 0 / 0 | 0 / 32 / 0 |
| TCP, 256 B | 280 / 280 / 0 | 0 / 0 / 0 |
| TCP, 1024 B | 0 / 0 / 0 | 1048 / 0 / 0 |
| Authenticated UDP, 32 B | 0 / 0 / 0 | 0 / 0 / 0 |
| Authenticated UDP, 256 B | 0 / 0 / 0 | 0 / 0 / 0 |
| Authenticated UDP, 1024 B | 0 / 0 / 0 | 0 / 0 / 0 |

Primary TCP 256 B has median 0.1167 allocated B/completed send (also B/unique receive here); all other median per-send figures are zero in these short warmed runs. The supplementary TCP 1024 B outlier is retained: 1048 B total, 1.4556 B/send. This is evidence that this warmed native byte path is small on this platform, **not a whole-module zero-GC guarantee or a per-message upper bound**. No source of individual tiny/outlier allocations is asserted without a trace.

An earlier unpaced-warmup smoke deliberately remains in the evidence: UDP 1024 B received only 101/128 warmup packets on one edge and failed with an explicit timeout. The final harness paces warmup at 1,000 ticks/s outside measurement, then demands complete warmup delivery; the subsequent smoke and all repeats passed. No measured UDP retransmission or reliable fallback was added.

Raw evidence under `Artifacts/NetRpcTransportBaseline/`:

- `primary-300ticks/suite.json`: 21 complete primary cases and environment metadata
- `supplementary-60ticks/suite.json`: 21 complete supplementary cases and environment metadata
- `summary.json`: grouped raw repetition values, normalized medians, window/rate ranges and independently cross-checked counters
- `smoke-paced-warmup/`: final 7-case functional smoke
- `smoke/`: retained initial smoke, including the real warmup-drop failure artifact
- `validation/`: builds, self-tests and SHA-256 source/binary manifests; `source-and-binary-paced-warmup.sha256` describes the binaries used for all final runs

Commands for the exact repeated groups:

```sh
dotnet Artifacts/bin/NetRpcTransportBaseline/Release/net10.0/NetRpcTransportBaseline.dll \
  --iterations 600 --warmup 256 --repeats 3 --rates 300 \
  --output Artifacts/NetRpcTransportBaseline/primary-300ticks
dotnet Artifacts/bin/NetRpcTransportBaseline/Release/net10.0/NetRpcTransportBaseline.dll \
  --iterations 180 --warmup 256 --repeats 3 --rates 60 \
  --output Artifacts/NetRpcTransportBaseline/supplementary-60ticks
```

No production source, main performance runner, shared solution, Godot or Unity file was changed by this diagnostic task. The generated/woven full-module benchmark remains the authority for module-level and per-role allocation budgets.
