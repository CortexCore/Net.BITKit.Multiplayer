# Arena V2 validation

Date: 2026-09-27. Independent codebase `Net.BITKit.Multiplayer`; no changes to Project B Unity or production backend.

## Automated checks

- Release solution build: zero warnings, zero errors.
- Core tests: 36 passing.
- Lobby tests: 3 passing (auth/expiry/persistence/real DMTP contract calls).
- Game tests: 16 passing (authority inputs/collision/respawn, actual woven RPC arrays and health, late join, callback lock order,
  same-socket admission, directory-ready broadcast barrier, leave ACK/abrupt close, failures and concurrent disconnect/damage).
- Total: **55** tests. Core C#9/netstandard2.1 and transport netstandard2.1/net8.0 still build.

## Actual multi-process graphic run

Command: `dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --visual`.

Final passing artifact directory: `Artifacts/ArenaRuns/20260927-041648-6531d6/` (a prior full visual run also passed at `20260927-040929-1c2181`).
Five child OS processes: Lobby, Dedicated Host, Alice (2D Raylib), Bob (3D Raylib), Charlie (headless late-joining mover).
The orchestrator separately calls the Lobby's remote business interface for registration/login/wrong-password/guest checks.

Verified from independently written reports:
- Host advertised a room and accepted ticket identities on the actual game socket.
- Host and Client A/B agreed on membership; dedicated Host had no local player.
- Client fire reached the Host, actual bullets/hits were broadcast, and Client HP decreased through woven SyncVar.
- Charlie joined later, saw current state and replicated movement to Host and Alice.
- Bob reached zero HP, then respawned with restored health.
- Normal Client exit removed the actor from every remaining node; other Clients then exited and Host had an empty room.
- Host withdrew its Lobby room on shutdown; all game reports had an empty Error string.
- Host report: 680 ticks, 63 shots, 23 hits, maximum 3 players, final 0 players and 0 bullets.

Images `alice.png` (2D) and `bob.png` (3D), 1200×800, were read back and visually inspected. They show the same arena players,
health bars, bullet rendering, room identity, tick and damage counters. Native renderer was NVIDIA OpenGL 3.3 on Windows x64.
The automated run uses bot input, not a claim of manual WASD/F2/mouse playtesting.

Launcher smoke: `Start-Arena.ps1 -NoBuild -DedicatedHost -Clients 2` brought up its processes and ready files, then accepted
stdin Enter and cleaned up its owned processes. Native image export supports caller-specified absolute paths; the first attempted
TakeScreenshot implementation's path concatenation bug was corrected and the final artifacts verified.

## Review/integration corrections

- Fixed callback lock inversion between authority tick and session lifecycle during damage/disconnect.
- Health restored via actual SyncVar after late join; sparse or equal-tick position data cannot retire/recreate tombstoned actors.
- Admission uses prepare/directory acknowledgment before Host All broadcasts, avoiding RPCs arriving before Client ready.
- Added acknowledged same-socket leave: queuing a leave frame is not proof the Host processed it. Physical TCP-close cleanup covers abrupt exits.
- Core ignores only writes already invalidated by explicit peer retirement, while active-peer transport failures remain observable and tested.
- Failed tick or room lease ends the session instead of advertising an apparently-ready frozen room.

## Remaining boundaries

Lobby is an in-process-account development service, with optional account-file persistence, not the production backend.
Sample movement and projectile records currently use reliable RPC at 20 Hz; prediction/interpolation and high-rate UDP are future work.
SyncVar collection mutation remains unsupported. Unity/UniTask/IL2CPP/AOT integration and Internet deployment are not part of this run.
