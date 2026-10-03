# LiteNetLib DIRECT isolated worktree validation (2026-10-03)

Source is the verified **316-file uncommitted snapshot** `D:/Iris/Documents/GitHub/Net.BITKit.Multiplayer/Artifacts/AgentBaselines/20261003-074225/manifest.json`; there is no shared HEAD or commit. Worktree `D:/Iris/Documents/GitHub/Net.BITKit.Multiplayer.LiteNetLib`, branch `feature/litenetlib-direct`. Only copied modified/new paths should be integrated; primary GC work remains independent.

## Fresh blocker-fix verification (2026-10-03)

Worker model exposed by agent environment: `openai/gpt-6.1-sol`. All edits and build outputs remain in the isolated worktree; no commit/push and no copy to primary. Primary's visible human demo on 28770 was not touched: automated E2E selects ephemeral ports.

Fixed four independent review findings: subscriber-thread early draining/ABBA, close-before-lifetime-attach (including wrapper late subscribers), wrapper constructor draining into an empty receiver, and poll failure leaving a live socket. Early dispatch now has one endpoint poll owner; receive/close callbacks are outside the transport lock; shutdown is exception-safe and supports callback disposal without self-await. Wrapper inner receive subscription is deferred until downstream installation and its closed state is retained.

- `dotnet test Tests/LiteNetLibTests/LiteNetLibTests.csproj -c Release --nologo`: **15 passed, 0 failed, 0 skipped**, retaining all seven previous tests. New coverage uses actual loopback and the actual sample wrapper: poll-lock-controlled early drain with callback disposal and ordered old/new delivery; bare/wrapped close-before-Runtime attachment and actual `PeerDisconnected`; 64 subscription/end races; distinct ordered wrapper early frames; throwing receive plus throwing close subscribers across two peers; explicit shutdown with throwing close; reentrant endpoint disposal from transport close. Private readbacks assert manager stopped, peer table empty and poll task complete; binding a new UDP socket to the stopped endpoint's port verifies actual socket release.
- `dotnet build Projects/BITKit.Multiplayer.LiteNetLib.csproj -c Release --nologo`: passed both netstandard2.1/net8.0, no warnings/errors.
- `powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Headless -LiteNetLib`: rebuilt all sample assemblies and Godot import, **passed actual double-Godot headless E2E**. Summary: `Artifacts/NetRpcGodot/20261003-083155-litenetlib-direct/summary.json`. Host PID 18996; Godot PIDs 31456, 27664. `Passed=true`, `Backend="LiteNetLib DIRECT"`, both clients `RealGodot/WovenCombat/HostAuthority/Movement/Pickup/Attack/MonotonicRevisions=true`, Health2=80, Inventory101=1, Score1=10. Client1 Reconnected=true, RecoveredCollectionGaps=1, drops/reorders=11/16; client2 UnauthorizedDenied=true, DisconnectObserved=true, drops/reorders=13/18. Per-client JSON and per-process logs are beside summary.
- Native sample regression after wrapper change: `dotnet Artifacts/bin/NetRpcGodot.E2E/Release/net10.0/NetRpcGodot.E2E.dll` **passed**, `Artifacts/NetRpcGodot/20261003-083220-direct/summary.json`; Host 25676, Godot 30684/31392. Same command with `--relay` **passed**, `Artifacts/NetRpcGodot/20261003-083259-relay/summary.json`; Relay 2592, Host 15088, Godot 29096/25604.
- `dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo -m:1` passed; `dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo -m:1`: **251 passed, 0 failed, 2 existing optional measurements skipped**. Adapter's 15 tests remain separate (total 266 passed across both commands). An initial parallel solution build hit CS2012 on CodeGen's shared PDB; serial retry passed, with no source workaround.

### Complete integration allowlist relative to frozen `sources`

**New (7):**

1. `Src/LiteNetLib/LiteNetLibDirect.cs`
2. `Src/LiteNetLib/Net.BITKit.Multiplayer.LiteNetLib.asmdef`
3. `Projects/BITKit.Multiplayer.LiteNetLib.csproj`
4. `Tests/LiteNetLibTests/DirectTests.cs`
5. `Tests/LiteNetLibTests/LiteNetLibTests.csproj`
6. `Docs/litenetlib-guide.md`
7. `Docs/litenetlib-validation.md`

**Modified sample composition (8):**

8. `Samples/NetRpcGodot/Session/ArenaSession.cs`
9. `Samples/NetRpcGodot/Session/ImpairedTransport.cs`
10. `Samples/NetRpcGodot/Session/NetRpcGodot.Session.csproj`
11. `Samples/NetRpcGodot/Host/Program.cs`
12. `Samples/NetRpcGodot/E2E/Program.cs`
13. `Samples/NetRpcGodot/Godot/ArenaView.cs`
14. `Samples/NetRpcGodot/Start-Lab.ps1`
15. `Samples/NetRpcGodot/README.md`

This correction pass edited only items 1, 4, 5, 6, 7, 9. `git diff --no-index` comparisons were against primary's **frozen** `Artifacts/AgentBaselines/20261003-074225/sources`, not primary's evolving source. Newline-normalized patch/stat checks show no content changes in Core, CodeGen, native/Relay sources, prior tests, Tools or shared slnx; raw comparisons also expose snapshot/worktree CRLF-vs-LF differences on existing files, which are not integration candidates and were not rewritten in this pass. Copy only the 15 paths above, never the whole snapshot, build outputs or `.godot` cache. Primary `Tools/NetRpcPerformance` belongs to the coordinator and is not included.

No remaining blocker from the four reviewed issues. This pass did not rerun visible windows or claim Unity/AOT, public-network, soak or real-network GC validation. Earlier visible results below are historical; current-source evidence is the fresh headless rerun above. LiteNetLib Relay is still explicitly unsupported.

## Previous implementation evidence (before this correction pass)

- `dotnet build Projects/BITKit.Multiplayer.LiteNetLib.csproj -c Release --nologo`: passed netstandard2.1 and net8.0 (pinned LiteNetLib 1.3.5).
- `dotnet test Tests/LiteNetLibTests/LiteNetLibTests.csproj -c Release --nologo`: **7/7 passed**; real UDP loopback reliable ordering, immediate outbound buffer overwrite, 1 MiB + 25 B reliable frame, Unreliable MTU limit/rejection, fast datagram, shutdown/reconnect/stale callback isolation, cancel accept/connect, concurrent sends/two independent rooms, and pre-attach packet delivery.
- `dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo`: passed (one pre-existing CS0067 warning); the new project/test are not added to shared slnx by design.
- `dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo`: passed **251 tests, 0 failed, 2 pre-existing optional measurements skipped**. New 7 adapter tests run separately (above).
- `powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Headless -LiteNetLib`: passed two real Godot 4.6.1 .NET clients. `Artifacts/NetRpcGodot/20261003-075719-litenetlib-direct/summary.json`.
- Final rebuilt-source headless rerun also passed: `Artifacts/NetRpcGodot/20261003-080611-litenetlib-direct/summary.json`; Host PID **6832**, Godot PIDs **12196, 29152**.
- `powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -LiteNetLib`: passed two visible real Godot clients, `Artifacts/NetRpcGodot/20261003-075757-litenetlib-direct/summary.json`; Host PID **6672**, Godot PIDs **11388, 16608**; `client1.png`, `client2.png` and per-process logs alongside JSON. Summary `Passed=true`, `Backend="LiteNetLib DIRECT"`, `Mode="Direct"`. Both clients have `RealGodot`, `WovenCombat`, `HostAuthority`, `Movement`, `Pickup`, `Attack`, `MonotonicRevisions` true; Health2=80, Inventory101=1, Score1=10. Client 1 Reconnected=true, RecoveredCollectionGaps=1, DroppedDatagrams=11, ReorderedDatagrams=16; client 2 UnauthorizedDenied=true, DisconnectObserved=true, DroppedDatagrams=13, ReorderedDatagrams=19.
- Native regression: E2E `dotnet Artifacts/bin/NetRpcGodot.E2E/Release/net10.0/NetRpcGodot.E2E.dll` passed at `Artifacts/NetRpcGodot/20261003-075825-direct`; `--relay` passed at `Artifacts/NetRpcGodot/20261003-075937-relay` on retry (one earlier run `20261003-075843-relay` timed out during post-reconnect snapshots; no native/relay files changed).

Earlier LiteNetLib visible run `20261003-075303-litenetlib-direct` exposed an initial-packet-before-AttachPeer race on reconnect; bounded pre-attach buffering fixed it; subsequent headless and visible passed. Evidence before the fix must not be presented as passing. Unity Editor/Player/IL2CPP were not verified here; no representative real-network GC report was measured. LiteNetLib Relay remains unsupported. Benchmarks, core GC tuning, and Unity installation are coordinator work.
