# Arena V2.1 interpolation validation

Date: 2026-09-27. Scope: local Arena presentation only; no core RPC, authority simulation, network protocol or Unity changes.

## Implementation

- Raylib-independent `Samples/Arena/App/ArenaPresentation.cs` consumes detached RoomSnapshot values with caller-supplied monotonic time.
- One fractional view clock for players/bullets at 20Hz, 100ms default buffer. Host local-player bypass; Client-only display-pose impairment.
- 32 history / 32 pending sample bounds, stale input handling, finite aim interpolation, stop-at-latest when no future sample exists.
- Immediate membership/Health metadata; anchored reset for spawn, Peer/Scope changes, same-tick resurrection, large warp and bullet lifetime reuse.
- Large backlog rebase handles both render stalls and network silence whose new sample arrives later than the first resumed frame.
- F3 compares interpolation and direct rendering with the SAME delayed/jittered samples. It does not disable pose impairment.
- Authority/bot inputs/report Final are separate from RenderView and Presentation.Frame.

## Tests run

- Full Release build: 0 warnings, 0 errors.
- Existing core 36 + Lobby 3 + Game 16 regressions passed.
- Final presentation suite: **17 passed**. Covers deterministic 60Hz fractional movement versus 20Hz steps, delayed/jittered delivery,
  monotonic view time, bounded history, hold on missing samples, delayed initial spawn and reset anchors, same-tick Health, no resurrection,
  immutability, Host-owned bypass, opposite aim directions, toggling, network-silence/render-pause recovery.
- Aggregate tested cases: **72**. Presentation tests link the same helper source used by the App; no graphics/native context is needed.
- The steady-motion test checks actual per-frame movement deltas (about 0.33 units/frame at 60Hz vs 0/1 unit steps).
  The delayed late-interval test requires at least 23/30 sampled frame deltas to be fractional.

## Real multi-process visual run

Command:

```shell
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --visual --compare-interpolation --pose-delay-ms 120 --pose-jitter-ms 60
```

Passed: `Artifacts/ArenaRuns/20260927-054213-c1d151/` (preceding validation run also passed at `20260927-053833-ab8c03`).
Separate Lobby, Dedicated Host, 2D Alice, 3D Bob and late-joining mover Charlie. Alice/Charlie interpolate; Bob renders direct.
Reports verify fractional interpolation occurs, direct mode counts zero interpolated frames, buffer bounds hold, and
presentation membership/Health match the independently retained authority data. The existing admission, motion, shooting,
damage, death/respawn, late-join, disconnect, and room-close checks pass with the pose delay/jitter enabled.

Native 2D/3D screenshots were captured and read back. HUD shows INTERP vs DIRECT and POSE DELAY/JITTER 120/60ms.
This is not a full packet latency/RTT simulator and does not demonstrate input prediction or lag compensation.
Images establish rendered states and diagnostics; quantitative smoothness is established by deterministic frame-delta tests, not a single still image.

Manual controls remain WASD/mouse/F2/F3. Automated verification used scripted bot inputs and the tested toggle API rather than claiming human keyboard playtesting.
