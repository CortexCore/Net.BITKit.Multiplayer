# Arena V2 Lobby (development sample)

Build and test only the Lobby-owned projects:

```sh
dotnet build Samples/Arena/Lobby/Arena.Lobby.csproj
dotnet build Samples/Arena/LobbyClient/Arena.LobbyClient.csproj
dotnet test Samples/Arena/LobbyTests/Arena.LobbyTests.csproj
dotnet Artifacts/bin/Arena.Lobby/Debug/net10.0/Arena.Lobby.dll --port 17890 --ready-file lobby-ready.json
# optional account persistence: append --data accounts.json
```

The executable binds **127.0.0.1** only; `--port` defaults to 17890. Its ready file is written after listening starts, containing only `{ "Ready": true, "Port": 17890 }`. Ctrl-C stops the process. `ArenaLobbyClient.ConnectAsync(address, port, token)` returns a concrete `IArenaLobbyApi` over TouchSocket 4.3.9 TCP DMTP RPC; callers must dispose it. Transport verification uses the fixed `ArenaProtocol.LobbyToken` only for protocol compatibility, **not** account authentication. `ListRoomsAsync` returns an empty array for an invalid session because the immutable contract has no list-result error DTO.

Without `--data`, registered accounts, guest identities, sessions, rooms and tickets are **all in memory** and disappear on exit. With `--data`, **only registered accounts** (username, stable PlayerId, random salt and PBKDF2-SHA256 120,000-iteration password hash) are persisted as JSON with temporary-file replacement. Protect that file using normal OS file permissions. Passwords and session/host/ticket bearer credentials are never persisted. After restart, users log in again; guests, sessions, room registrations and tickets do not survive. No migration or distributed/multi-instance persistence is provided.

Sessions expire in 12 hours, rooms 45 seconds after creation/last heartbeat, tickets in 15 seconds and on first successful redemption. Room host tokens work only while their room is alive. A failed wrong-host or wrong-room attempt leaves the legitimate ticket valid. Admission returns the account/guest PlayerId with a **fresh PeerId per redemption**; the game host must bind it to the *actual* authenticated game transport connection, not trust caller-supplied socket IDs. API calls have a five-second RPC timeout; a timeout does not guarantee that a write was rolled back. RoomInfo is copied out of the authority, not an editable live reference.

Development bounds: 10k accounts, 2,048 live sessions, 256 rooms, 4,096 pending tickets, and 120 password-hash auth operations/minute per server, with a per-username failed-login throttle. Guest names and room details are length/character checked. This is **not** an Internet-facing identity service: there is no TLS, durable DB, IP-level rate limiting, audit trail or multi-host coordination. Run only on trusted local networks (default loopback) and do not use real passwords.
