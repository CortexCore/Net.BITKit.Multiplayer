# LiteNetLib DIRECT (independent extension)

The adapter now lives in the sibling independent repository `Net.BITKit.Multiplayer.LiteNetLib`. Its root `Net.BITKit.Multiplayer.LiteNetLib.csproj` targets `netstandard2.1;net8.0`, pins LiteNetLib **1.3.5** and UniTask **2.5.10**, and references Core. The existing native Transport and Core projects do not reference this adapter. Use `BITKit.Multiplayer.LiteNetLibDirect`, **not** the legacy root `BITKit.Multiplayer.ITransport`.

Keep the main library and extension checkouts side by side. The extension's `.csproj` compiles `Src/**/*.cs`; `Src/package.json` is the UPM root (`net.bitkit.multiplayer.litenetlib` 0.1.0). The main solution, Godot Session and integration test project reference the sibling extension. Build/test commands from the main checkout:

```powershell
dotnet build ../Net.BITKit.Multiplayer.LiteNetLib/Net.BITKit.Multiplayer.LiteNetLib.csproj -c Release --nologo
dotnet test ../Net.BITKit.Multiplayer.LiteNetLib/Net.BITKit.Multiplayer.LiteNetLib.slnx -c Release --nologo
dotnet test Tests/LiteNetLibTests/LiteNetLibTests.csproj -c Release --nologo
```

The extension tests are independent of Godot. The last command links those adapter tests and adds the actual sample wrapper integration cases. See [extraction record](litenetlib-repository-extraction.md); the old [worktree validation](litenetlib-validation.md) is dated historical evidence.

```csharp
await using var listener = LiteNetLibEndpoint.Listen(28770);
var hostTransport = await listener.AcceptAsync(cancellationToken);
hostRuntime.AttachPeer(nextFreshLogicalPeerId, hostTransport); // >= 2; one unique ID per connection

var (endpoint, transport) = await LiteNetLibEndpoint.ConnectAsync("127.0.0.1", 28770, cancellationToken);
// Keep endpoint alive for the entire client session, attach as peer 1.
clientRuntime.AttachPeer(1, transport);
// On exit: dispose runtime and await endpoint.DisposeAsync().
```

Each endpoint owns one `NetManager`, one PollEvents loop and its connections. The listener owns all server-side transports; the client owns a private endpoint. `Closed` transitions once per connection; each subscription racing that transition is either registered for it or notified immediately after close, outside the transport lock. This includes late Runtime attachment and the sample impairment wrapper. `NetPeer.Id` is never used as a persistent application identity; the Host allocates logical IDs and uses the runtime's `PeerDisconnected` for business cleanup. Initial pre-attach traffic is copied into a bounded 32-frame queue. Subscription only schedules a drain: the endpoint poll owner drains older frames before newer borrowed ingress, never on the subscribing thread and never while holding the transport lock. Events after subscription borrow the LiteNetLib reader until synchronous callback return. No asynchronous consumer may retain the borrowed memory. The reader is recycled in `finally`, including subscriber failure. A full pre-attach queue disconnects the peer.

The sample `ImpairedTransport` installs its downstream receiver before subscribing to inner ingress, preserving pre-attach frames instead of draining them into an empty event. It retains its closed state for future lifetime subscribers and cancels delayed ingress on close. A receive subscriber exception terminates the endpoint: the poll loop's `finally` stops the manager/socket, closes all connections (even if individual close subscribers throw), and clears accepted/drain queues. Receive callback disposal stops the endpoint synchronously without waiting for its own poll task; external disposal also waits for loop completion. No Core or native TCP/Relay behavior is changed.

`Send` maps to `ReliableOrdered` (LiteNetLib fragments/reassembles); `SendFast` to `Unreliable` (no retry, reliable fallback or application fragmentation). LiteNetLib 1.3.5 `NetPeer.Send(ReadOnlySpan<byte>, ...)` synchronously copies all segments into its own packet pool. Completion ends the source buffer loan, **not** remote delivery or acknowledgement. Concurrent sends serialize per peer; the adapter rejects sends after close, canceled sends and frames larger than 25 B + 1 MiB. `UnreliablePayloadLimit` comes from the current peer MTU (`GetMaxSinglePacketSize(Unreliable)`), not the native TCP+UDP 60000 B limit. An oversized Unreliable frame throws rather than changing channel. NetRpc handles its own scope, map, reply, state, collection and authorization exactly as before; timeout never implies no remote side effect.

Sample launcher: `powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Headless -LiteNetLib`. Omit `-Headless` for actual viewport PNGs. The same script without `-LiteNetLib` preserves native TCP Direct and `-Relay` paths. LiteNetLib + Relay is rejected explicitly.

UPM safety: the extension's `Src/Net.BITKit.Multiplayer.LiteNetLib.asmdef` retains `defineConstraints: ["BITKIT_LITENETLIB_DIRECT"]` and `autoReferenced: false`. Install the extension as a separate local/Git UPM package (`?path=/Src` for Git). Supply LiteNetLib 1.3.5 as a Unity-compatible auto-referenced DLL, or add the matching source-assembly reference to this asmdef. Install Core/UniTask, configure the project-level scripting define and explicitly reference the adapter from the consuming asmdef. .NET tests do not validate Unity dependency wiring or AOT; verify actual Unity compilation/session and Player/IL2CPP separately.
