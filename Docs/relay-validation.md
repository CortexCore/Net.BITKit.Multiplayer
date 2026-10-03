# Relay validation record

Date: 2026-09-27. Implementation by three Sol workers, architecture/integration and independent review coordinated by Astra.
Only independent Net.BITKit.Multiplayer changed; no Unity or production backend deployment.

## Build and automated tests

Release solution build: 0 warnings / 0 errors, including core netstandard2.1 and TouchSocket netstandard2.1/net8.0.
Latest full Release suite: **98 passed**: Core 36, Relay 16, Lobby 9, Arena Game 20, Arena Presentation 17.

Added checks cover real TouchSocket DMTP framing/token handling; reserved/active room publication; two-room isolation;
bad credentials, incorrect scope/host, spoofed-source/pre-admission gameplay rejection; queue bounds and overflow completion;
canceled/timed-out admission and late accepted result cleanup; Host/Relay close; closing-room re-registration; normal leave ordering;
Lobby and Relay TLS rejecting untrusted/mismatched certificates with explicit certificate validation.

TLS rejection tests do not establish a trusted-chain positive roundtrip. No machine trust store was modified.

## Six-process, forced-Relay visual E2E

Artifact directory: `Artifacts/ArenaRuns/20260927-075921-25aee3/`.
Processes: independent Lobby, Relay, Headless Host, 2D Alice, 3D Bob, late-joining Charlie.
The configured Host Direct port was deliberately occupied by an unrelated dummy listener in the harness throughout the test.

Verified:
- Lobby account registration/login/wrong password and guests.
- Room appears only after relay-backed Host is active.
- All endpoint reports identify Relay and its actual port; zero connection attempts reached the dummy Direct listener.
- Input -> Host simulation -> All RPC position/bullets and SyncVar Health work.
- Late join, interpolated/direct-display comparison with 120ms pose delay and ±60ms jitter, death/respawn and cleanup work.
- 2D/3D framebuffer screenshots show ROUTE RELAY; captured images were read back for inspection.
- Every game process exited cleanly with an empty report Error; Lobby withdrew the room.
- Final Relay stats: 5,030 forwarded application messages / 5,717,543 bytes; 0 rooms / 0 clients / 0 pending admissions.

## Separate fault-injection processes

- Kill Relay: `Artifacts/ArenaRuns/20260927-080026-ed5b70/` — passed.
- Kill Host: `Artifacts/ArenaRuns/20260927-080027-1d08cf/` — passed.

Clients lose readiness and clear rendered actors; received gameplay stops advancing; room is delisted;
Direct dummy listener still gets no connection. Host-loss also empties the Relay route tables. Fault reports are expected for these injected failures.

Direct route regression: `Artifacts/ArenaRuns/20260927-080523-5fa324/` — passed full headless gameplay and cleanup.
The first Direct regression attempt uncovered an E2E file-monitor problem: Windows readers held report files without FileShare.Delete,
occasionally preventing the App's atomic rename. The harness now allows shared read/write/delete and the test passed.

Launcher smoke: `Start-Arena.ps1 -NoBuild -Clients 2` started a player Host and two windows, auto-selected Relay,
and followed the Lobby descriptor without client route flags. The Enter shutdown path cleaned its owned processes.

## Corrections made during integration

- Replaced initial worker deviation (private TCP framing) with actual TouchSocket DMTP.
- Explicit TLS verification; TargetHost alone was insufficient with TouchSocket 4.3.9 defaults.
- Admission rollback removes only the matching committed member/health binding; disconnect-over-ACK race is handled.
- Closing room remains reserved until its external close callback completes.
- Relay graceful leave ACK no longer disposes the physical wire ahead of RpcRuntime cleanup, avoiding spurious Host-loss callbacks.

## Boundaries

No NAT hole punching, UDP relay, direct fallback, live connection migration or Host migration. No actual VPS/cross-network test yet.
Steam invite entrypoints can share RoomId routing but are not implemented by this sample. Lobby persistence and authentication remain sample-grade.
TLS options build and reject bad certificates; public deployment and valid-domain TLS still require actual server configuration and validation.
