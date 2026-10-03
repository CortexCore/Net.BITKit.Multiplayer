# Reliable game-message binary migration

User explicitly requests removing JSON strings and JToken from reliable game messages. The JSON path is our RpcRuntime, not a requirement of TouchSocket DMTP. Migrate room traffic end-to-end: calls, replies/errors, SyncVar state/snapshots, member directory and target removal. Keep Lobby/account remote APIs and disk diagnostic JSON outside this change.

## Contract

- Use a bounded, versioned binary routing/control envelope and **MemoryPack 1.21.4 for typed business values**. Do not invent a second DTO serializer or tunnel JSON inside binary frames. Reject old JSON and unsupported envelope versions explicitly; matching room participants must upgrade together. Keep Unreliable version 2 compatible unless a verified defect requires a separate change.
- No remote CLR type names. Argument types come from bound methods/interface proxy declarations, result types from pending calls, SyncVar types from registered properties. Relay can forward opaque typed value bytes while preserving original requester/permissions.
- RPC direction, ownership, scope, ready/hold barrier, return/null semantics, cancellation, timeout, remote errors, late binding, state versions, remove tombstones and exact Host-local execution remain intact.
- Reference DTOs use generated `[MemoryPackable] partial` schemas; unmanaged values/primitives use MemoryPack built-ins. Reliable DTOs need bounded strings and nested arrays/DTOs for Arena RoomSnapshot, unlike the stricter Unreliable flat schema. SyncVar still rejects collections and uses whole-value replacement.
- Preserve 1 MiB envelope, 16 KiB synchronized value, 256 array/segment entries, 256 members, 512 tombstones and bounded argument/schema depth limits. Validate hostile lengths/counts **before** a formatter can allocate attacker-sized arrays. Prefer supported MemoryPack reader APIs and schema validation; any preflight walker is a bounds validator, not a homemade value serializer. Reject unsupported/custom/polymorphic/cyclic schemas explicitly. Include forged nested-count tests, not only top-level bounds.
- Remove Newtonsoft.Json/JToken references from Src/Runtime and the core project's dependencies. Core remains C#9/netstandard2.1. Test-only legacy JSON injection may remain to prove rejection; disk reports and unrelated Lobby code can still use their existing serializers.
- Cache stable schema/method metadata where needed so switching serialization does not create new per-call reflection discovery. Avoid encode -> string -> encode, unnecessary per-recipient copies, and unbounded buffers. The existing IRoomWire byte[] ownership contract must remain safe through asynchronous sends; do not recycle pooled frames while transports still use them. Zero-GC is not presumed.
- Preserve a bounded immutable encoded state value for pre-bind buffering and mutation isolation, compare by value/encoded bytes to suppress unchanged updates, distinguish missing/void from valid nullable returns. Avoid JSON token substitutes implemented as generic dynamic object trees.

## Ownership

- Core Sol: Src/Runtime including Contracts, CodeGen, Projects/BITKit.Multiplayer.csproj, core Tests/{RuntimeTests,UnreliableRuntimeTests,Fixtures,Contracts,InvalidFixtures,...} and those test projects. Implement codec + integrate all core paths and migrate tests meaningfully. Do not alter Transport/TouchSocket/Arena files.
- Arena Sol: Samples/Arena/Contracts business DTO MemoryPack attributes/generator dependency (PlayerPose/BulletPose/RoomSnapshot), Game/App/E2E/GameTests only as required. Do not annotate live diagnostic counters or alter Lobby protocol semantics. Add representative DTO/snapshot tests and deployed generated codec validation. No core/weaver edits.
- Astra: architecture acceptance, readback/source audit, full tests, same-load native Direct/Relay profiles against prior transport baselines, usage/migration/GC documentation.

## Acceptance

No Newtonsoft/JToken on the core game runtime path or core dependency. Actual woven Host/All/Target/interface calls, nullable results, SyncVar scalar/DTO and late join tests pass on binary traffic, old JSON rejected, malformed/truncated/oversized/nested bomb payloads rejected without huge allocations or invoked handlers. Native UDP auth/DI/rebind regression unchanged. Real Arena native Direct/Relay still passes gameplay/cleanup and fresh profile traces no longer show our reliable RPC Newtonsoft paths. Quantify remaining allocations rather than equating binary with zero-GC. Do not silently weaken tests that formerly mutated JSON packets; migrate their injections to the real binary codec.
