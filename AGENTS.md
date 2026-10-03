# BITKit Multiplayer development

## Required documentation routing

- Start repository work with `Docs/ai-integration-handoff.md` and `Docs/current-status.md`; then read only the current domain guide and named code entrypoints for the task. `Docs/README.md` / `Docs/SUMMARY.md` are the human/GitBook navigation.
- Current API behavior is summarized in `Docs/api-contracts.md`. Architecture authority remains `Docs/architecture.md`; observed source/test behavior must be checked before changing either.
- Historical design/validation pages are indexed in `Docs/history-index.md`; their "latest" counts and old protocol descriptions are dated checkpoints, not current requirements.
- Unity work starts with `Docs/unity-integration-plan.md`, then the target Unity repository's own AGENTS/overlays/skills and live MCP checks. Do not infer Editor/IL2CPP success from this library's .NET tests or placeholder UPM metadata.
- After an implementation or integration milestone, update current status, affected API/guide pages and the focused evidence record. Leave a concise next-step handoff; do not force the next conversation to reread all historical logs.

## Development constraints

- Architecture authority is `Docs/architecture.md`. Do not silently substitute Mirror/NGO's dual-role Host model.
- Product: BITKit Multiplayer; namespace `BITKit.Multiplayer`; package `net.bitkit.multiplayer`.
- Use apply_patch for ordinary source changes. Preserve unrelated work. No commits/pushes without request.
- This is an independent .NET library, not a Unity project. Use `dotnet build` and `dotnet test` here.
- Shared runtime must build for netstandard2.1 without Unity references; tests/tools use modern .NET.
- Default asynchronous technology is UniTask / UniTask<T> (Cysharp 2.5.10). New business RPCs and the new NetRpc runtime/receiver/transport pipeline should remain UniTask end-to-end. Task/ValueTask are explicit compatibility, BCL I/O, test/process-harness or IAsyncDisposable boundaries, not a default business return type; do not hide AsTask conversions inside the UniTask hot path. RPC return-type changes require both peers to regenerate/reweave. Preserve the legacy B6/TouchSocket API while migrating the new backend.
- No modification of sibling business-server or Unity project code unless explicitly included in the task.
- Host is the authority peer, including a dedicated host. Host is NOT also a local Client.
- No global active-runtime or global target routing dictionaries. Each runtime/scope owns its endpoints and lifecycle.
- User-authored Docs/design-v1.md and Docs/design-v2.md govern the new NetRpc backend. Its reliable/unreliable byte transport is native TCP+UDP, with generated DI interfaces, ordinary-class IL wrappers, Relay and ECS/interface state. Read Docs/design-implementation.md and its validation page. The old design-v2-agent-handoff is historical progress, not a restriction to v2.
- TouchSocket/B6 remains the legacy integration backend. Test new NetRpc with actual generated/woven assemblies and native TCP/UDP/Relay loopback; retain real TouchSocket tests for legacy regression. Do not infer KCP/RUDP implementation from an abstraction.
- Unsupported RPC/SyncVar declarations must produce clear build-time diagnostics; never silently fall back to ordinary local execution.
- Interface and concrete-instance calls must have identical semantics. Preserve method tokens and scoped dispatch semantics.
- Document exactly what is implemented/tested vs future Unity/AOT/UniTask integration.
