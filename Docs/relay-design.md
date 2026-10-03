# Relay / transparent room routing — implementation contract

Astra architecture. Independent .NET repository only. User-facing joining stays RoomId/invitation -> lobby -> descriptor -> connection.
Player-host/co-op defaults Relay in Arena App; dedicated (`--no-local-player`) defaults Direct. `--route direct|relay|auto` is diagnostics.
ArenaHostOptions defaults Direct for backward-compatible library/test callers.

## Invariants

- Relay is an authenticated router, NOT a room Peer and never a game/RpcRuntime authority. All includes Host once.
- Both Host and Client dial OUT to Relay. Relay-mode Host MUST NOT open a direct game listener.
- Connection-bound identity and room scope define routing, not frame Source claims. Client sends only to its room Host;
  Host may send to admitted clients in that room. No cross-room forwarding, Host spoofing or pre-admission gameplay.
- TCP/DMTP only; no punching, auto direct fallback, live path/Host migration or UDP tunnel.
- Host disconnect removes room/clients immediately and notifies lobby. Relay loss invalidates wires/runtime. No old ID reuse.

## Parallel ownership

1. Relay worker: Src/TouchSocket/Relay*.cs EXCEPT RelayContracts.cs (Astra), Tests/RelayTests project. No old transport/Runtime edits unless needed and reported.
2. Lobby worker: Samples/Arena/Lobby, LobbyClient, LobbyTests; NEW Samples/Arena/Relay executable and tests.
3. Arena worker: Samples/Arena/Game, GameTests, App. No shared Contracts/Relay implementation/E2E/launchers/solution edits.
4. Astra: shared contracts, solution, E2E, launchers, docs and final validation.

## Core Relay APIs (exact agreed signatures)

Namespace BITKit.Multiplayer.TouchSocket, existing TouchSocket assembly. Shared options/authorization types in RelayContracts.cs.

```csharp
public sealed class TouchSocketRelayServer : IDisposable
{
    public TouchSocketRelayServer(IRelayRoomAuthorizer authorizer);
    public Task StartAsync(RelayListenOptions options, CancellationToken cancellationToken = default);
    public RelayStatistics GetStatistics();
    public event Action<string> Diagnostic; // sanitized
}
public sealed class RelayHostWire : IRoomWire
{
    public Task ConnectAsync(RelayConnectOptions options,
        Func<string, Task<(PeerId Peer, string Response)>> admission,
        Action<PeerId> admitted, Action<PeerId>? admissionAborted = null,
        CancellationToken cancellationToken = default);
    public Task ActivateAsync(CancellationToken cancellationToken = default);
    public event Action<PeerId> PeerPrepared;
    public event Action<PeerId> PeerDirectoryReady;
    public event Action<string> AdmissionFailed;
    // Standard IRoomWire. Received carries authenticated original Client PeerId.
}
public sealed class RelayClientWire : IRoomWire
{
    public RelayClientWire(PeerId host);
    public Task ConnectAsync(RelayConnectOptions options, CancellationToken cancellationToken = default);
    public Task<string> RequestAdmissionAsync(string ticket, CancellationToken token = default);
    public Task NotifyPreparedAsync();
    public Task NotifyDirectoryReadyAsync();
    public Task NotifyLeavingAsync(CancellationToken token = default);
    // Standard IRoomWire. Received identifies logical HostPeerId, not Relay.
}
```

Host Connect registers a RESERVED relay room with HostCredential. Authorizer verifies and returns Scope/HostPeerId, matched against descriptor.
One authenticated socket owns the room; another cannot replace it. Not joinable until Activate after Host Runtime/callbacks initialized.
Server invokes authorizer.ActivateRoomAsync; only success activates. Disconnect removes routing before awaiting CloseRoomAsync.

Client Connect selects an active RoomId/Scope but cannot send gameplay before admission. RequestAdmission routes its bounded ticket to
the authenticated Host using an opaque request correlation tied to the requesting socket. Host invokes its existing admission callback
(RedeemTicketAsync), replies with approved PeerId/response; Relay binds that PeerId to that socket. Client cannot choose identity.
Host admitted callback runs BEFORE gameplay forwarding and successful client admission response. Handle late callback, timeout, duplicate
request/peer, limits and cleanup. Forward preparation/directory-ready controls. Leave ACK happens after Host retires logical peer, not just
after queuing the frame. Physical Client/Host/Relay close always raises PeerLeft correctly once.

Use reserved protocol numbers distinct from direct 200..206. Bounded envelopes and per-connection bounded ordered writer preserve control/data
order, apply message/byte queue limits and disconnect/clean slow overloaded peers. No unbounded task/queue accumulation. Registry locks must
not encompass external callbacks or awaited IO. Stats count actual forwarded gameplay messages/bytes, not handshake only.
TLS: server PFX+password; clients use normal certificate validation and configured TLS target host. Never accept-any-certificate by default.
Default bind loopback, explicit public bind configuration. No credentials or payload dumps in diagnostics.

## Lobby extensions

LobbyClient implements IArenaRelayLobbyApi; IArenaLobbyApi direct calls unchanged.
- ReserveRelayRoomAsync allocates identity/host token but hides room from ListRooms and rejects JoinRoom until published.
  RoomInfo Address/Port now contain selected Relay endpoint, never player's private Host IP.
- ValidateRoomHostAsync returns detached RoomInfo only for correct live HostToken, including pending rooms.
- PublishRelayRoomAsync publishes only a reserved Relay room with correct HostToken. Invoked by Relay authorizer after activation.
- Heartbeat handles reserved and published rooms. Expiry/Close remove either. Wrong-token/room rejection doesn't consume valid join tickets.

Relay executable `Samples/Arena/Relay/Arena.Relay.csproj`:
`--port 17892 --lobby-address 127.0.0.1 --lobby-port 17890 --bind 127.0.0.1 --ready-file path --report path`.
Optional `--tls-pfx path --tls-password-env VAR`. Reports raw RelayStatistics JSON every ~250ms, no secrets.
Authorizer calls Lobby ValidateRoomHost/PublishRelayRoom/CloseRoom with Host credential. Fail startup if Lobby unavailable.

## Arena integration

Host Relay: reserve -> RelayHostWire outbound Connect with existing ticket callbacks -> Authority/Runtime/bindings -> Activate publishes
room -> tick/heartbeat. Client gets descriptor/ticket normally, selects wire internally from ConnectionMode, runs SAME readiness/snapshot flow.
No direct fallback. A small sample-local control facade is fine; no generic transport plugin system.
IArenaSession.ConnectionMode / ConnectedEndpoint must reflect actual established wire; report and HUD show route.
App Host `--route auto|direct|relay` defaults auto: player Host Relay, --no-local-player Direct. Add --relay-address/--relay-port/--relay-tls.
Client never overrides route. Existing Game tests using Direct ArenaHostOptions still work; FakeLobby needn't implement Relay extension.

## Acceptance

1. Existing direct suites pass; real Relay server + outbound Host + multiple Clients carry woven RPC/SyncVar.
2. Reject inactive/wrong scope rooms, wrong Host keys, pre-auth messages, cross-room targets, spoofed source, duplicate Host/Peer.
3. Two rooms share one Relay port without leaks; forwarding ordered and buffers bounded.
4. Host/Relay disconnect completes waits and breaks communication, never falls back direct.
5. Real process Arena E2E Relay mode. Occupy the configured Host game port with a dummy listener so direct Host binding is impossible.
6. Relay stats >0, actual route shown in reports/HUD, killing Relay breaks connected game; separate failure and successful runs.
7. Interactive launch defaults Lobby+Relay+player Host+Client, Dedicated defaults Direct. Preserve interpolation/F3.
8. TLS build/config and deployment docs; public Internet/NAT testing awaits a real server and cannot be claimed from loopback.
