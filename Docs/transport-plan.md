# Replaceable packet transport implementation contract

User explicitly requests ITransport + DI, native C# UDP first. This supersedes the original TouchSocket-only packet-I/O constraint. Reliable TouchSocket sessions remain the control plane. Do not implement KCP now or remove UDP endpoint verification.

## Shared API / ownership

`Src/Runtime/TransportContracts.cs` is the authoritative contract (Astra-owned). ITransport owns one bound endpoint. ITransportFactory is replaceable via DI and creates fresh owned endpoints, including rebind; no shared singleton live socket. Received payload is borrowed during its synchronous callback. Send may borrow caller bytes through completion. Dispose signals stop without blocking a callback, StopAsync/Completion await release of receive and send resources. Unsupported delivery fails explicitly. UDP supports Unreliable only; future transports must state their capabilities.

Native implementation: `Src/Transport/UdpTransport.cs` + `UdpTransportFactory`, namespace BITKit.Multiplayer.Transport, compiled by Projects/BITKit.Multiplayer.Transport.csproj (netstandard2.1;net8.0, C#9). Use System.Net.Sockets, a single reused full 65536-byte receive buffer per endpoint; consume callbacks before reuse. Reject packets beyond options.MaxPacketBytes and socket truncation errors. Do not shrink receive buffers to the application limit. Reuse SocketAsyncEventArgs/awaitable operations where feasible; do not add unbounded Task.Run per packet or queue. At most options.MaxConcurrentSends; explicit overload rejection. Closing interrupts receive and terminates pending OS sends before returning their buffers. Expose honest rental/in-flight statistics.

DI registration in native assembly: `services.AddUdpTransport()` registers a stateless `ITransportFactory` (TryAdd so custom registrations can win). Factory creates via new rather than resolving disposable transient endpoints from a root provider, which would retain them for the entire container lifetime. Callers pass resolved factory to room-wire constructors; optional factory arguments retain source compatibility and default to native UDP.

UdpLane keeps HMAC/challenge/proof/nonce/expiry/scope/enable gates and buffers. Replace only UdpSession I/O with ITransport. Direct Host/Client, Relay Host/Client and Relay server take optional factory and reuse it on rebind. Failed bind/unsupported capabilities dispose the created endpoint. Legacy TouchSocketUdpTransportFactory implements the SAME interface solely for explicit compatibility/performance A/B. No automatic fallback.

TCP admission supplies identity and UDP credentials, NOT a proven UDP endpoint. Keep UDP binding challenge/proof, readiness wait, heartbeat and reliable-coordinated fresh credentials for rebind. Binding packets are not a second login protocol.

## Worker ownership

- Native worker: Src/Transport + Tests/TransportTests and its project. Main owns shared contracts/project/solution. Test real sockets, borrowed buffer reuse, max packet, concurrent sends, shutdown idle/pending, cancellation, unsupported reliable mode, factory DI replacement and rental recovery.
- Integration worker: Src/TouchSocket + Tests/DatagramTests/RelayTests. Wire constructor optional factory, UdpLane I/O integration, legacy adapter and factory. Existing UDP test override becomes Func<ITransport,IPEndPoint,ReadOnlyMemory<byte>,Task>. Keep auth/replay/rebind and callback lock regression coverage. Test injected counting factory all paths incl rebind and separate rooms.
- Arena worker: Samples/Arena Game/App/Relay/E2E + relevant sample tests. Add --transport native|touchsocket (default native), compose ITransportFactory through a real IServiceCollection/provider, pass to room constructors. Arena report endpoint implementation should be visible without secrets; profile-window preserves selection. Existing shared Arena Contracts may add only string transport diagnostic/options (no core networking dependency in Contracts). Run actual A/B profiles after others compile.
- Astra: contract, projects/solution, design review, final build/tests, readbacks, GC comparison/docs.

## Acceptance

Full reliable suites and actual UDP tests pass, real Direct/Relay preserve binding and no TCP fallback, rebind creates a new native endpoint and keeps identity, rentals settle after shutdown, injected factories are used (no bypass). Run same Release --profile --transport native vs explicit touchsocket under Direct and Relay; retain raw stacks/GC and business evidence. Do not claim zero GC, no UDP handshake or KCP support. Native removes per-packet 64KiB allocation; other JSON/HMAC/boxing may remain. No Unity changes or verification in this phase.
