# Unified room routing and Relay

Player-host/co-op rooms now default to Relay in Arena App. Dedicated hosts (`--no-local-player`) default to Direct.
Clients select a RoomId and follow the Lobby descriptor for the Direct/Relay route; no manual route choice, punching or automatic fallback. Packet I/O selection is separate: native UDP is the default, and `--transport touchsocket` is an explicit comparison option. See [ITransport](transport-guide.md).

## Local one-click test

From the repository root on Windows:

- Double-click **Start-Arena.cmd**: Lobby + Relay + player Host + one Client, with two graphic windows.
- Double-click **Start-Arena-Relay.cmd**: explicitly force the same Relay route.
- **Start-Arena-Latency.cmd** adds the existing client display-pose delay/jitter test; networking still uses Relay by default.

```powershell
# Coop player Host and two Clients, through one local Relay
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -Clients 2

# Dedicated Host and two Clients, default Direct
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -DedicatedHost -Clients 2

# Diagnostic override: even a dedicated Host can use Relay
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -DedicatedHost -Route Relay -Clients 2
```

The HUD/ready file/report show actual `ConnectionMode` and `ConnectedEndpoint`. Relay-mode Host only dials outbound;
it never opens the configured direct game listening port. Host/Client roles and logical PeerIds are unchanged.

## Separate processes

Build once with `dotnet build Net.BITKit.Multiplayer.slnx -c Release`, then run these in separate terminals:

```shell
dotnet Artifacts/bin/Arena.Lobby/Release/net10.0/Arena.Lobby.dll --port 17890
dotnet Artifacts/bin/Arena.Relay/Release/net10.0/Arena.Relay.dll --port 17892 --lobby-port 17890 --report Artifacts/relay.json
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role host --name Host --route relay --relay-port 17892
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role client --name Alice
```

Reliable Relay traffic uses TouchSocket TCP/DMTP protocol 220 and a bounded envelope, forwarding the Runtime packet opaquely. Gameplay UDP uses the injected packet transport, native by default, on the same numeric server port. Relay never invokes gameplay methods; RPC permissions remain with the game Host/receiver. The existing Direct wire retains its own controls.

## Admission and publication

1. Host reserves a hidden Lobby room, obtaining room identity, Scope and a host credential.
2. Host connects outbound to Relay. Relay validates that credential with `IRelayRoomAuthorizer` and reserves ownership for that socket.
3. Host initializes authority/Runtime/bindings, then activates the route. Relay publishes the room through the authorizer only after this step.
4. Client obtains its short-lived one-use ticket from Lobby and connects to the advertised Relay endpoint.
5. Relay forwards the ticket on the authenticated room Host channel. Host redeems it and approves a fresh PeerId;
   Relay binds the approved identity to the actual requesting socket. Client cannot assert arbitrary source PeerId or another room.
6. UDP credentials and endpoint proof establish the datagram lane, then prepare/directory-ready barriers complete before broadcasts. Leave is acknowledged after Host retires the member. This lifecycle ACK is distinct from a void RPC: void gameplay calls do not wait for application success replies.

Each room uses the same Relay port, with isolated maps. Relay is not a room member. Client-to-client Target routing still passes
through the game Host, so permissions and original-sender semantics are identical to direct mode.

Host disconnect immediately invalidates its route and clients; closing the Lobby room occurs through the authorizer.
A closing RoomId cannot be replaced while its old close callback is outstanding. Relay loss invalidates connected wires;
the game Host withdraws its room and clients lose readiness. No path migration, Host migration or Direct fallback exists in this phase.

## Limits and diagnostics

Default RelayListenOptions: 128 rooms, 64 clients per room, 1 MiB application message, per-connection queue limits of
128 messages and 4 MiB. Ordered writer queues disconnect over-limit peers rather than growing without bounds.
Pending admission and frame input are bounded; rejected/late approval paths roll back the matching provisional game member.

Relay report fields: Rooms, Clients, PendingAdmissions, ForwardedMessages, ForwardedBytes, RejectedMessages.
UDP forwarding/bound/drop/size fields and UdpForwardingEnabled are reported separately; `--udp-control-file` toggles gameplay forwarding only. Typed borrowed-memory queue ownership is described in [typed RPC](typed-rpc-guide.md).
Forwarded counters measure actual gameplay forwarding, not only connection handshakes. Diagnostics contain categories, not credentials or payloads.
Disconnects and rejected frames can contribute to rejection diagnostics; a nonzero count alone does not prove a game-state failure.

## Deployment preparation

Relay and Lobby are .NET 10 executables. For example:

```shell
dotnet publish Samples/Arena/Lobby/Arena.Lobby.csproj -c Release -r linux-x64 --self-contained false -o Artifacts/publish/lobby
dotnet publish Samples/Arena/Relay/Arena.Relay.csproj -c Release -r linux-x64 --self-contained false -o Artifacts/publish/relay
```

Install the corresponding .NET runtime on the VPS, copy the published outputs, and run Lobby/Relay as supervised services.
Defaults listen on loopback. Public listeners require explicit `--bind 0.0.0.0`; Lobby needs its TCP port and Relay needs both TCP and UDP on its configured port. UDP authentication is not encryption; reliable credential/control traffic needs the configured TLS protection.
Game Hosts and Clients make outbound connections; users do not need to open a home-router game port.

TLS listener flags for either service:

```text
--tls-pfx /etc/bitkit/certificates/service.pfx --tls-password-env CERT_PASSWORD_ENVIRONMENT_VARIABLE
```

Configure a certificate whose DNS name matches the public endpoint and whose chain clients trust. Passwords come from the named
environment variable, not command-line values. Relay-to-Lobby supports `--lobby-tls --lobby-tls-target <certificate DNS name>`;
Arena App supports the same Lobby flags, plus `--relay-tls` on Host when reserving its room. Clients infer Relay TLS from the descriptor.
TouchSocket 4.3.9's permissive default validation was explicitly overridden: connections accept only `SslPolicyErrors.None`.

For colocated services, Relay may contact Lobby over loopback with the appropriate TLS hostname override. Do not publish a
loopback Relay address in a public room: the Host must reserve with `--relay-address relay.example.com --relay-tls`.

This includes configuration entrypoints and certificate-rejection tests, **not a completed cloud deployment**. A positive certificate-chain
roundtrip and cross-ISP/NAT tests still need the actual server/DNS/certificate. The sample account service is not a production account platform.

## Reproduce acceptance

```shell
# Real Relay gameplay with native windows, while the Host direct port is deliberately occupied
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --visual --compare-interpolation --pose-delay-ms 120 --pose-jitter-ms 60

# Fault injection: neither scenario may fall back to Direct
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --kill-relay
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --kill-host

# Preserve Direct path
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj
```

See [typed-rpc-validation.md](typed-rpc-validation.md) for the current integration checkpoint. [relay-validation.md](relay-validation.md) is the earlier reliable-Relay record; remaining public-network validation has not been completed merely by the later loopback tests.
