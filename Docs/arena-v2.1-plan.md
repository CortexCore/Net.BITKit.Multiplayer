# Arena V2.1 — small presentation-only interpolation

This phase implements ONLY Arena smoothing, not a generic state-group framework, prediction, rollback, ECS migration or a new transport.
Authority, RPCs, Health SyncVar and projectile damage simulation remain unchanged.

## Ownership

- Sol: `Samples/Arena/App` code and new `Samples/Arena/AppTests` project/tests. Do not change root solution, Contracts, Game, Lobby, core or CodeGen.
- Astra: Contracts diagnostics (already added), solution, E2E integration, launch scripts, docs and independent review/validation.

## App behavior

- Add an engine-independent `ArenaPresentation.cs` under App (no Raylib dependencies). AppTests can compile/link this same file with Contracts,
  avoiding a new core subsystem or package. Use a caller-supplied monotonic time for deterministic tests, no real sleeps.
- `ArenaController.View` remains authoritative for bots, HP/reporting and gameplay inputs. Introduce RenderView used only to draw poses.
- Client renders positions/aim directions with default 100ms interpolation. Host's own LocalPlayerId uses current authority directly;
  other drawn entities may interpolate. No synthetic Host client.
- Players and bullets share ONE view clock at 20 ticks/second. Bounded histories (e.g. 32 snapshots) and bounded pending injected snapshots.
- Accept new-tick pose samples; do not keep adding duplicate session.GetView returns at 60Hz. Ignore stale snapshots for authoritative lifecycle.
- Interpolation uses a delayed fractional render tick between snapshots. It must be a progressing clock, not only a function of latestTick:
  evaluating multiple frames between incoming updates must produce continuous movement. Never move view clock backwards on jitter/old arrivals.
- No infinite extrapolation. Hold latest sample when exhausted; bound frame catch-up or reset after a long stall. Movement and aim must remain finite.
- New spawn, new scope, changed PeerId, Deaths change, dead→alive respawn, and large position jumps (~6 units) reset relevant histories.
  Ignore delayed samples from the prior lifecycle so respawn/teleport cannot glide back toward old position.
- Current authoritative membership and Health/Deaths override presentation metadata immediately, including same-tick SyncVar changes.
  Removed players/bullets cannot reappear from pending old snapshots. Clear buffers on disconnect/session replacement.
- Bullets can interpolate by stable Id + BornTick; no local collision or hit logic. Active membership still comes from live authority view.
- Do NOT mutate input snapshots or objects returned by IArenaSession. Return detached RenderView values.

## Delay comparison

- CLI `--interpolation-ms 100` (0..500), `--no-interpolation`, `--pose-delay-ms 0` (0..1000), `--pose-jitter-ms 0` (0..500).
- F3 toggles interpolation at runtime; reset/re-align cleanly when toggling, no temporal jump backwards beyond intended buffer transition.
- Apply delay/jitter only to the DISPLAY POSE sample stream of Client, after existing real network receipt. This is NOT a full RTT simulator:
  input submission, health, admission and transport are unaffected. Label HUD and docs explicitly `POSE DELAY/JITTER`, not network RTT.
- Deterministically schedule delivery with seeded pseudo-random bounded jitter; actual late/reordered snapshots should be handled by the buffer.
- Disabled interpolation still uses the same delayed/jittered pose stream, so comparison is fair. Direct display then visibly updates at 20Hz.
- HUD shows interpolation on/off, buffer ms, render tick/history count, pose delay/jitter. Existing health/role/controls must remain legible.
- Populate ArenaReport.Presentation fields supplied in Contracts; Frame is RenderView. Existing Final stays authority.
  InterpolatedFrames counts frames which actually interpolate moving state between samples, not simply a checkbox enabled.

## Tests

Add `Arena.AppTests.csproj` (net10 xUnit) referencing Contracts and linked ArenaPresentation source (or App internals if needed without loading Raylib).
Deterministic assertions: 20Hz constant speed → fractional 60Hz frame motion; disabled mode staircase with identical delivered samples;
delay/jitter and out-of-order stability; bounded buffers and end-of-stream hold; default/reset/scope/disconnect;
teleport, death/respawn, same-tick health update; removed actor/bullet no resurrection; Host-owned bypass; immutable inputs.
Quantify smoothness in a steady middle interval using per-frame deltas/step change, don't just assert an enum.

Astra will run existing real multi-process E2E with delay/jitter options and inspect 2D/3D captures. All work stays in independent Multiplayer repo.
