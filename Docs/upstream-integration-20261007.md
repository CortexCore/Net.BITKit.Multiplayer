# GitHub integration validation — 2026-10-07

## Bases and preserved changes

- Core GitHub `main`: `d57974b81d3857c0fba79855cf3da4e4593706dd`
- Unity GitHub `main`: `dcb7b513a425737ab91692ed15e0b41dc037d95f`
- LiteNetLib sibling used for integration tests: `0af9b0a7cdd15b200b18c1ca73d1ddf7646e560e`

The Core changes originally prepared against `37d817a` were integrated into a clean checkout of the newer GitHub main. The upstream LiteNetLib repository extraction remains intact. Original development worktrees were not overwritten. This source change contains the scalar comparison-cache allocation optimization, synchronization-worker disposal handling, engine-neutral object protocol/readiness, regression tests, reproducible diagnostic tools and a source-only Godot object adapter example. No character asset bundles, binaries, build caches or raw benchmark logs are included.

## Verification

Using .NET SDK 10.0.401:

- Full Release solution build passed. The pre-existing `VirtualRoomTransport.Received` CS0067 warning was emitted through the Core and sibling-reference paths; no errors.
- Full solution test rerun: **236 passed, 0 failed, 1 pre-existing skipped** (Core 184, LiteNetLib 15, native Transport 16, benchmark guards 21).
- Separate native transport diagnostic build and self-tests: **8/8 passed**.
- Actual dedicated Host + two Client processes, native Direct TCP/UDP, generated/woven network benchmark smoke: **11/11 passed**, including 100 combined callbacks for each replicated-state case. This short smoke establishes correctness, not a new performance comparison.
- Independent static review of Core lifecycle/readiness, ownership checks, stale-load cleanup, scalar cache ownership and source-only sample found no confirmed blocker. Changed-path secret-pattern and generated/binary-file checks were clear; both repositories passed whitespace checks.

The first clean full test exposed a pre-existing solution omission: `MixedBackendFixtures` was referenced by the test project but absent from `.slnx`, so the Release test could not find its fixture DLL. This integration adds that existing project to the solution. The following run hit the existing process-wide active-rental assertion in `Delayed_wire_cancellation_and_real_128_send_cap_release_all_rentals` (expected captured baseline 1, actual 0). An unchanged full rerun passed. The timing-sensitive failure is disclosed rather than counted as an uninterrupted green run; its root cause was not fixed here.

## Limits and next step

Unity changes received static API/thread/lifecycle review only. Unity Editor import/compile, actual Host/two-Client object acceptance, domain reload, Player and IL2CPP remain unverified. The historical Godot object-engine evidence is documented separately; this integration did not repeat engine execution. The new object protocol is not compatible with the former Unity-only service: deploy matched Core and engine adapters to every peer and rebuild them together.

The allocation claim remains the controlled 2026-10-06 scalar-cache result: Host 32 B less per same-encoded-length update, with three-process scalar-state median 103.01 → 70.59 B per Host mutation. It is not a whole-module zero-GC claim. The known disconnect-time Broken pipe diagnostic remains unresolved.
