# .NET foundation validation

Date: 2026-09-27. Architecture/review: Astra. Implementation and independent reviews: Sol workers.

## Executed checks

- Solution Debug build: no warnings/errors; tests passed at the intermediate 33-case checkpoint.
- Final solution Release build: no warnings/errors.
- Final Release suite: **36 passed, 0 failed, 0 skipped**.
- Core built as `Net.BITKit.Multiplayer.dll` for .NET Standard 2.1 with C# 9.
- TouchSocket adapter built as `Net.BITKit.Multiplayer.TouchSocket.dll` for .NET Standard 2.1 and .NET 8.
- Standalone Cecil tool weaved the Release ConsoleRoom output from the repository root.
- Woven ConsoleRoom used real TouchSocket TCP/DMTP loopback and printed `Host result: 42`.

## Covered behavior

- Actual compiled assemblies transformed and loaded before invocation; no emulated-wrapper-only evidence.
- Concrete and interface calls, multiple interface aliases, inherited/explicit/closed-generic interface contracts,
  overloads, inherited private receive handlers, nested RPCs, branching and try/catch/finally.
- Host, All and Target routing, Host exactly once, Host relay preserving requester, declared-route enforcement,
  receiver authorization, owner policy, wrong-room/state injection rejection.
- Async return values/faults and call context restoration, null results, cancellation/timeout while SendAsync stalls,
  disconnect/unbind/disposal completion, observable remote void faults.
- Versioned member directory and identity lookup, duplicate admission preservation, readiness demotion/re-ready,
  terminal revocation/stale-snapshot rejection, and retired Peer ID rejection.
- Host-authoritative SyncVar, unchanged suppression, late join and late bind, stale state filtering,
  pre-bind construction, denied Client setters and detached instance protection.
- RPC-only target removal, removal received while unready, late admission tombstones and rejected late bind.
- Concurrent runtime lifecycle mutations, two actual DI scopes, bounded state/pending entries and isolation from
  process-global JSON settings.
- Build diagnostics from actual invalid compiled fixtures; repeated weaving rejected for RPC, role-only and state-only assemblies.
- Real TouchSocket session admission, configured IPv4 bind address, RPC and state transfer; deterministic tests also
  use a small in-memory wire to inject failures and ordering scenarios.

## Review-driven corrections

Independent reviews found and reproduced route relabeling, unready relay/state access, cancellation registrations
after stalled send, duplicate admission rollback, first-interface-only IDs, missing repeat-weave markers,
constructor SyncVar initialization failure, RPC-only removal gaps and stale readiness after revocation. These
paths were corrected and regressions added. Release tests now use Release fixtures rather than Debug outputs.

## Not asserted

This is the first pure .NET foundation, not a finished Unity multiplayer rollout. Unity package import, ILPP adapter,
UniTask, main-thread scheduling, IL2CPP/AOT/trimming, debug-symbol preservation and Project B migration are not verified.
The existing backend account/store/currency service was not changed. Real production authentication and game admission
remain the application's responsibility. Collections/deep mutation tracking and wire-version upgrade interoperability are deferred.
