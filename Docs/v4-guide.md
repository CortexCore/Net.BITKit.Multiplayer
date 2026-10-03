# V4 usage — Unreliable RPC and MemoryPack

Normal woven calls have since moved to **typed B6/v4 on both Reliable and Unreliable lanes**; the earlier B4/v2 UDP codec is an explicit compatibility path. See [typed RPC guide](typed-rpc-guide.md) for current notification, schema and ownership behavior.

Packet I/O has since evolved to DI-selected **native UDP by default**, with the reliable TouchSocket control plane retained. See [ITransport usage](transport-guide.md) and [native/legacy A/B validation](transport-validation.md). The protocol semantics below still apply.

## Start the sample

Double-click `Start-Arena.cmd` (player Host through Relay), `Start-Arena-Relay.cmd` (explicit Relay), or use:

```powershell
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -DedicatedHost -Clients 2 -Route Direct
```

The launcher builds/restores the complete deployment graph. After adding a package, do not deploy a stale `--no-restore` output: the App needs `MemoryPack.Core.dll` and the corresponding `.deps.json` entry. E2E checks this explicitly.

WASD/mouse play; F2 switches view, F3 interpolation, F4 toggles local application UDP, F5 rebinds a Client endpoint. TCP remains connected when gameplay UDP is paused. Clients wait for authenticated UDP binding before opening the ready broadcast barrier.

Direct uses the game TCP port and the **same numeric UDP port**. Relay uses its TCP port and same numeric UDP port; the Host opens outbound connections only. These are distinct OS sockets. No UDP delivery falls back to TCP. The sample default listeners are local development endpoints. UDP uses HMAC authentication, **not encryption**; public credential/control traffic needs TLS. Relay supports configured TLS; the current Direct adapter requires a trusted external TLS tunnel for a secured public control plane. Internet/NAT traversal is not established by the loopback tests.

## Authoring

```csharp
public struct PoseUpdate
{
    public int Id;
    public float X, Z, AimX, AimZ;
}

[Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)]
public void ApplyMotion(long tick, ArraySegment<PoseUpdate> updates)
{
    // Apply only to known entity lifetimes; reject stale per-entity ticks here.
}
```

- Reliable remains the default. Its former JSON codec has since been replaced by **binary v3 + MemoryPack**, including SyncVar values; see [reliable migration](reliable-binary-guide.md). All room peers must upgrade together.
- Unreliable supports **void only**, with the existing Host/All/Target roles, scope and authorization. All executes on Host once and ready Clients. Receiver arrival lane must match the declaration. No reply waiters, automatic retransmission, or reliable fallback.
- Use scalar/unmanaged value DTOs for motion; these need no MemoryPack generator or annotation. Flat reference DTOs require a consumer reference to `MemoryPack.Generator` 1.21.4 and generated `[MemoryPackable] partial` declarations. Shared runtime depends on `MemoryPack.Core` 1.21.4 and remains C#9/netstandard2.1; the tested generated consumer uses modern .NET. Unity/source-generator/AOT integration is separate work.
- Supported reference DTO members are scalar/unmanaged. Nested reference DTOs, reference-bearing struct DTOs, nested strings and collections are rejected by weaving. Public Span/pointer RPC and SyncVar collections remain unsupported.
- `ArraySegment<T>` writes **Offset..Offset+Count**, never backing-array capacity. Encoding finishes before the void call returns, so the caller may reuse its source array immediately. The runtime retains its own encoded byte rental until every fanout send ends.
- MemoryPack serializes typed parameters selected by registered methods. Our bounded routing envelope still handles method/target/scope; no remote CLR type names are accepted.
- Application codec **version 2** is incompatible with the old V4 JSON prototype. Upgrade all room participants together. Matching method/DTO schemas are required; there is no schema negotiation/migration layer.

## Limits and batching

The adapter advertises `IRoomDatagrams.MaxUnreliablePayloadBytes = 900`; total authenticated wire datagrams are at most 1200 B. Oversized calls are rejected, not fragmented. `RpcRuntime.MeasureUnreliableCall(key, method, args)` uses the exact codec so a producer can split a reusable array into fitting segments. Measurement has a 64 KiB candidate ceiling. At most 32 arguments and 256 entries per top-level array/segment are accepted.

In-flight limits: 128 application sends per runtime, 64 UDP data sends per lane, 4 control sends per grant, and 64 Relay UDP forwards globally. Overload does not enqueue an unlimited history. See the [GC report](v4-gc-report.md) for ownership, packet-pool retention and actual allocation measurements.

Arena sends compact MotionIds/positions at 20 Hz with reused arrays. Roster/identity maps, bullet spawn/end, Health, input and initial full snapshots are reliable. Unknown MotionIds cannot spawn objects; per-entity tick checks reject stale UDP. Missing batches do not remove entities.

## Diagnostics and verification

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --rebind-udp
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --pause-udp --rebind-udp --visual
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --soak-seconds 90
```

App automation: `--udp-off-after`, `--udp-on-after`, `--udp-rebind-after` take seconds. Relay's `--udp-control-file <path>` reads a local admin-owned `on`/`off` file; it is not a remote endpoint. E2E uses it to freeze motion while proving reliable probes and Health continue.

`Artifacts/ArenaRuns/<run>/summary.json` contains per-process datagram/GC observations and pause/rebind evidence. `Gc` measures the **whole Arena game process**, not solely the codec or transport; no production `GC.Collect` is used. Report history is bounded. Socket statistics reset after a successful socket rebind; gameplay batch counters continue. `PositionSendAllocatedBytes` is a synchronous send-thread counter and does not include subsequent asynchronous work on other threads. Separate tests instrument pool ownership directly.
