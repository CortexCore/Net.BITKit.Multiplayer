# Arena V2 — shared contract and ownership

Owner: Astra. Implementation: three parallel Sol workers. This sample is isolated from Project B business and Unity repositories.

## Goal

Real multi-process Lobby, Host, Client A, Client B with .NET 10, TouchSocket and Raylib-cs 8.1.0.
Move with WASD, aim/fire with mouse, see authoritative movement, bullets, health/death/respawn and late join.
Host can display the same scene or run headless. Host is NEVER a synthetic local client.
Raylib belongs only to App. Core simulation is headless and unit-testable.

## File ownership (do not cross-edit without coordinator)

- Astra: `Samples/Arena/Contracts`, this design, root solution integration, E2E orchestration and final docs/review.
- Sol Lobby: `Samples/Arena/Lobby`, `Samples/Arena/LobbyClient`, `Samples/Arena/LobbyTests`. No Game/App/core modifications.
- Sol Gameplay: `Samples/Arena/Game`, `Samples/Arena/GameTests`, minimal CodeGen support for bounded RPC arrays/DTO arrays,
  and minimal TouchSocket admission hook if needed. Do not alter Contracts/App/Lobby or root solution.
- Sol View: `Samples/Arena/App`, `Samples/Arena/AppTests` if useful. Do not modify Game/Lobby/Contracts/core or root solution.
- Projects compile only own source and use shared Directory.Build.props artifact output. Names: Arena.Contracts, Arena.Lobby,
  Arena.LobbyClient, Arena.Game, Arena.App, Arena.LobbyTests, Arena.GameTests. Assembly names may use BITKit.Multiplayer.Samples.Arena.*.

## Stable integration APIs

Shared namespace: `BITKit.Multiplayer.Samples.Arena`. Contracts source is authoritative.

LobbyClient implements:
```csharp
public sealed class ArenaLobbyClient : IArenaLobbyApi
{
    public static Task<ArenaLobbyClient> ConnectAsync(string address, int port, CancellationToken token = default);
    // all IArenaLobbyApi members
}
```
It must use real TouchSocket DMTP RPC for remote-interface business calls, no HTTP or simulation shortcuts.
Lobby executable: `dotnet Arena.Lobby.dll --port 17890 [--data path] [--ready-file path]`.
Return structured failures for expected validation. Accounts optional persistence: credentials PBKDF2 or equivalent salted hash,
never plaintext logs. Guest accounts/session tokens, expiring room host tokens, heartbeat expiry, short-lived single-use join tickets.
Only matching room HostToken redeems a ticket; wrong-room/bad-host attempts cannot consume a legitimate ticket. Lobby controlled
development sample, not existing account server. Network protocol token is NOT account identity.

Game implements:
```csharp
public static class ArenaSession
{
    public static Task<IArenaSession> StartHostAsync(IArenaLobbyApi lobby, AuthSession auth, ArenaHostOptions options, CancellationToken token = default);
    public static Task<IArenaSession> StartClientAsync(IArenaLobbyApi lobby, AuthSession auth, ArenaClientOptions options, CancellationToken token = default);
}
```
Lobby ownership stays with caller, sessions do not dispose the supplied lobby object. Snapshot copies are thread-safe and detached.
Host starts game transport, registers room and heartbeat, owns fixed tick, motion and collisions. Client requests ticket then connects
and authenticates the SAME actual TouchSocket connection before logical admission; never let a caller supply an arbitrary socket/session
ID to claim. If extending TouchSocket wire, keep it a minimal bounded application admission callback/request before `Admit`, preserve old
APIs and all core tests. Host maps ticket identity to actual session and uses fresh PeerId from redemption. Host and clients share Scope
and HostPeerId provided by room info. Client waits for authoritative ready directory before RPC.

Use actual woven methods:
- Submit input `[Rpc(SendTo.Host)]`, identity exclusively from authenticated RpcCallContext.Sender. Clamp input, finite checks,
  increasing sequence, current alive player and firing cooldown. Never accept PlayerId to damage/move arbitrary peers.
- Broadcast position batches and bullet spawn/end `[Rpc(SendTo.All)]` (bounded scalar DTO arrays allowed in RPC only).
- Late join `[Rpc(SendTo.Host)] Task<RoomSnapshot>`; tick guards prevent delayed snapshots rolling state back.
- Per-player ordinary state object `[SyncVar] public int Health { get; private set; }`; proper client bindings/snapshot visibility.
  Health display must derive from SyncVar; snapshots seed discovery and initial state, not a fake replacement for SyncVar tests.
- Apply gameplay under a serialized simulation lock/queue; never call Raylib from sockets/ticks. No separate physics package.
- XZ arena +/-12, speed ~4/s, bullet speed ~14/s, hit radius ~0.55, 25 damage, fire cooldown ~0.25s, respawn ~3s. Host LocalPlayer=false
  for dedicated spectator simulation. Spawn deterministic slots with two players facing across center so bots can aim reliably.
- Membership removal cleans player, bullets and binding; no duplicate host broadcast application, full snapshot includes active bullets.

Game.csproj must arrange REAL weaving of Game assembly before app/test load. Use CodeGen project build dependency. Do not weave a file
already loaded or repeatedly weave previous output. An after-build step can stage raw IntermediateAssembly as TargetDir/*.unwoven.dll
then run the tool to write TargetPath; resolver must see Contracts and dependencies. Only one build owner initially; coordinator builds
whole solution after all return. If RPC arrays require codegen update, allow bounded RPC DTO arrays but continue rejecting collections in
SyncVar recursively; report core modifications and add tests. Do not invent JSON string tunneling around unsupported method shapes.

## App UI and headless CLI

One executable `Arena.App` with:
```
--role host|client
--lobby-address 127.0.0.1 --lobby-port 17890
--name Alice
--auth guest|register|login [--username name --password value]
--game-port 17891 --room-name ArenaV2 --room <roomId>  # Client defaults to first available room
--headless --no-local-player  # latter only meaningful for Host
--view 2d|3d
--duration 20               # seconds; 0/unset runs until close/cancel
--bot idle|move|shoot       # deterministic scripted inputs for cross-process tests
--report <json-path>        # ArenaReport, write periodically (~250ms) and on orderly exit
--ready-file <path>         # room/role/player readiness JSON; no secrets
--capture <png-path>        # graphic window screenshot after ready + a few drawn frames, log the path
```
Default guest login, role host/client required. Console output readiness/connect/failure readable; never log tokens/passwords.
App starts lobby connection, AuthSession, ArenaSession using stable signatures above. Headless branch MUST NOT initialize Raylib.
Graphical mode has play area + clear sidebar HUD: role, peer/player IDs shortened, room, readiness, tick, HP, shots/hits, input help.
2D top-down circles and bullets; 3D ground grid + simple blocks/spheres + same HUD. F2 toggles 2D/3D without reconnect. Escape/window-close
disposes in correct order. Mouse aim converts to XZ; health bars and labels for all players. No external assets.
Bot move follows bounded pattern; bot shoot targets closest other alive player via GetView; one sequence counter per input channel.
Reports: JSON serializer uses Contract property names; include metrics MaximumPlayers and MinimumOwnHealth, LastError only real error.
Fail startup with nonzero exit. Report errors without silently replacing network state with local simulation.

## Acceptance (Astra)

1. Existing 36 tests plus Lobby/Game tests pass.
2. Start real Lobby + headless Host + two App clients as separate OS processes; auth, admission, ready directory, motion, shots/hits,
   health decrease/death/respawn observed in independently produced reports. Assert late join current state and disconnect cleanup.
3. Graphic host/client run in independent processes, capture 2D and 3D PNGs and inspect; don't claim interactive visual verification from
   headless tests alone. If desktop unavailable, report exactly the gap.
4. Provide reproducible run/build/weave commands and all-process launch script. Never start Unity or edit existing business server.
