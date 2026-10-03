# NetRpcTrace

Standalone .NET 10 EventPipe `.nettrace` analyzer. No runtime/Core dependencies, solution edits, or benchmark changes. Exact NuGet pin: `Microsoft.Diagnostics.Tracing.TraceEvent` **3.1.21**, with transitive lock file. The package was tested with actual .NET 10 EventPipe allocation, SampleProfiler, and EventSource marker events.

```powershell
dotnet restore Tools/NetRpcTrace/NetRpcTrace.csproj --locked-mode
dotnet build Tools/NetRpcTrace/NetRpcTrace.csproj -c Release --no-restore
dotnet run --project Tools/NetRpcTrace/NetRpcTrace.csproj -c Release --no-build -- --input Artifacts/capture.nettrace --output Artifacts/report.json --preview
```

Optional filters: `--process-id PID`, `--role host`, `--profile profileName`. Filters are exact and case-sensitive for role/profile. `--max-stacks 100` and `--max-depth 128` control JSON full-stack output, not sampled-event totals or named-frame aggregation. `--preview` prints at most ten allocation types and ten **Managed** profiler leaf methods per window. `--help` prints usage.

## Collection contract

Enable these providers in the collector:

- `Microsoft-Windows-DotNETRuntime`, Verbose, keywords `0x41000019` (GC, type names, stack, JIT, loader).
- `Microsoft-DotNETCore-SampleProfiler`, Informational.
- `BITKit-NetRpc-Benchmark`, Informational.
- Keep end-of-session CLR rundown enabled and gracefully stop collection so existing managed methods can resolve.

The benchmark EventSource must emit event **1** `MeasurementStart(string role, string profile, int operations)` immediately before measured work, and event **2** `MeasurementStop()` immediately after it. Stop may have extra payload; all raw marker payloads are retained. Start payload names are matched case-insensitively. Startup/warmup must precede start; final report serialization must follow stop. Windows are half-open `[start, stop)` and paired **within each PID**; successive windows and interleaved processes are supported. Nested, orphaned, incomplete, or nonpositive-duration marker pairs are errors, not silently repaired.

Unmarked captures are rejected by default. `--whole-trace` explicitly permits whole-trace analysis only when there are **no** markers (not malformed markers). It cannot supply role/profile metadata or a benchmark operations count. If markers exist, this flag does not override them.

## Reader/API and report semantics

The reader uses `new EventPipeEventSource(input)` and the actual 3.1.21 dispatcher overload `TraceLog.CreateFromEventTraceLogFile(pipe, temporaryEtlx, new TraceLogOptions())`. That package does **not** expose `ConvertEventPipeToTraceLogFile`; there is no guessed call to it. A converted `TraceLog` includes rundown/JIT symbol information before analysis. The intermediate ETLX directory is private, randomly named under the system temporary directory, and removed after analysis. The input capture is not modified.

Two passes over `TraceLog.Events` first preserve markers and construct windows, then aggregate typed `GCAllocationTickTraceData` and `ClrThreadSampleTraceData` events. All other events, including producer JSON output, are excluded from sampled-event aggregation. Each report preserves session UTC start, marker absolute UTC and relative milliseconds, exact per-process boundaries and metadata, full-trace vs in-window counts, runtime event version counts, reader lost-event/truncation indications, and explicit caveats.

Allocation schema verified against the installed assembly: `AllocationAmount` (Int32), `AllocationAmount64` (Int64), `TypeName` (String), `TypeID` (UInt64), `AllocationKind`, `ObjectSize`, and event `Version`. v2+ weight uses `AllocationAmount64`; older versions use unsigned interpretation of `AllocationAmount`. Missing type names/nonpositive weights are reported separately rather than fabricated. Valid samples and weighted bytes are distinct metrics. Type weights sum to valid weighted bytes. **This is sampled attribution, not exact per-type object bytes or exact bytes per operation**: ticks generally cover intervening allocation activity and can straddle window boundaries. `ObjectSize` is not the weight.

Stacks come from `event.CallStack()`, following `Caller` leaf-first. Named methods come only from `CodeAddress.Method?.FullMethodName`, resolved using captured metadata. No fabricated UNKNOWN mapping, symbol download, or substitution of the first resolved ancestor as an exclusive leaf. Unresolved frames retain raw hexadecimal address and any known module; missing stacks, unresolved frame occurrences/samples/leaves, depth truncation and omitted stack groups are counted. Inclusive methods are counted once per sample even in recursive stacks; their totals are not additive. Exclusive methods are only actual leaves. Full stack groups retain sampled count and weight. Full-stack groups at the depth cap can merge identical retained prefixes; the truncation flag makes this explicit.

Profiler weights are **sample counts**, not bytes or measured CPU milliseconds. All-frame tables retain separate Managed/External/Error kind counts; Managed-only inclusive and exclusive tables support the CPU preview without mislabeling parked/native thread samples as managed CPU. External can mean native execution **or waiting**, so all-sample rankings are not CPU utilization. `sampleProfileIntervalMs` is reader metadata only. Observed minimum/mean/maximum **same-thread** timestamp deltas are preserved; they are not inferred execution durations. Sampling overhead, scheduling, missing stacks/symbols and event loss limit conclusions.

## Standalone verification

```powershell
dotnet run --project Tools/NetRpcTrace/NetRpcTrace.csproj -c Release -- --self-test
# Supply an existing directory; writes only smoke.nettrace and smoke-report.json there.
dotnet run --project Tools/NetRpcTrace/Smoke/NetRpcTrace.Smoke.csproj -c Release -- C:/path/to/existing/evidence-directory
```

The guards reject malformed markers and verify interleaved PIDs, repeated windows, metadata, and half-open boundaries. The independent smoke workload collects a **real EventPipe stream**, runs the CLI in a separate process, reads JSON, and asserts dynamic marker pairing, PID selection, real nonzero allocation and CPU samples, resolved allocating/CPU method names, exclusion of distinct startup/report methods, and type-weight conservation. It is a reader sanity test, **not NetRpc performance evidence**. It uses the TraceEvent dependency's DiagnosticsClient API; it does not install/change `dotnet-trace`.

Additionally tested against the repository's existing `Artifacts/ArenaRuns/20260927-225135-08c7a6/profile-host.nettrace`: explicit whole-trace fallback read 148 valid allocation ticks and 16,024,192 weighted bytes. That old capture had no benchmark markers or SampleProfiler events; it is not a measured NetRpc result. New benchmark captures still need separate review.
