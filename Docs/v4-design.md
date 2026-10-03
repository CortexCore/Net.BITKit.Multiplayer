# V4 — Unreliable RPC, direct UDP and Relay UDP

Architecture owner Astra; implementation by Sol workers. Existing reliable API and backend remain compatible. No Unity changes.

Implementation checkpoint: see [V4 validation](v4-validation.md) and [GC report](v4-gc-report.md). The selected parameter codec is **MemoryPack.Core 1.21.4**, directly writing into the pooled writer; application datagram version 2 replaces the initial JSON prototype. Reliable JSON remains unchanged. Reference DTOs use consumer-generated MemoryPack schemas with deliberately bounded flat shapes; motion DTOs are unmanaged. This document records the original contract/ownership plan; measured implementation results are in the linked reports.

## Public contract (already added by Astra)

`RpcDelivery { Reliable, Unreliable }`, RpcAttribute.Delivery defaults Reliable.
`IRoomDatagrams` is an additional lane on the SAME room wire, not a transport plugin system; see Src/Runtime/DatagramContracts.cs.

```
[Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)]
void ApplyMotion(long tick, ArraySegment<PlayerMotionUpdate> poses) { ... }
```

Unreliable supports void only, all three SendTo directions with existing authority/role/scope checks. No result tasks, pending-reply table,
ACK/retransmission, reliable fallback or latest-only generic-method filtering. Pose receivers, not generic RPC, filter Tick/Sequence.
Arrival lane must match method declaration: reliable packets may not invoke an Unreliable declaration or vice versa.
Host executes its All body once locally. Target client-to-client goes through Host at Runtime level preserving sender, independently of Relay routing.

## Worker ownership

- UDP worker: Src/TouchSocket, existing direct/relay controls, new Udp*.cs, Tests/DatagramTests + existing RelayTests if necessary.
  Do NOT edit Runtime/CodeGen/Arena except shared RelayContracts stats already exist.
- RPC worker: Src/Runtime EXCEPT DatagramContracts.cs, CodeGen, Tests/Fixtures and core Tests; own pooled encoding/proxy route metadata.
  Do NOT edit TouchSocket or Arena.
- Arena worker: Samples/Arena/Game, GameTests, App, Relay executable (diagnostic UDP pause control only); do NOT edit Contracts/core/solution/E2E/docs/launchers.
- Astra: shared contracts, solution/launchers/E2E/docs, cross-module review.

## UDP wire implementation

Use real TouchSocket 4.3.9 UdpSession (package has SendAsync(EndPoint,ReadOnlyMemory<byte>,CancellationToken) and UdpReceivedDataEventArgs).
No TCP-wrapped datagrams and no separate homemade socket framework. Direct and Relay use the same authenticated datagram codec/session helper.
Default UDP listener uses SAME numeric port as existing TCP listener. Host/client ephemeral outbound endpoint sends binding controls;
Relay Host has only an outbound UDP socket, never binds its configured game listener port.

- Implement IRoomDatagrams on TouchSocketHostWire, TouchSocketClientWire, RelayHostWire, RelayClientWire.
- Start UDP as part of the existing connect/start lifecycle. Ship random per-admitted-connection session ID and MAC secret over existing reliable
  control messages. Relay credentials cover Host reverse socket and each admitted Client separately. Never log them.
- First binding uses endpoint reachability challenge/response authenticated by the session MAC; no routing based solely on claimed PeerId or IP.
- Credential grants only for authenticated/current reliable sessions. All data bound to room/session and real connection, invalidated on leave/close.
- Packet authentication (HMAC, constant-time tag compare), monotonically increasing packet nonce and bounded sliding replay window. Permit fresh
  out-of-order packets in that window. Replays don't execute twice or redirect endpoints. Reject expired/old-scope/bad-tag/oversize packets.
- RebindDatagramsAsync on client/reverseHost opens a fresh endpoint and coordinates fresh capability/binding through reliable control. Old bindings
  cannot overwrite new ones. Test this on actual UDP ports. Direct Host may explicitly reject rebinding its listening endpoint.
- Heartbeats maintain NAT binding and bounded endpoint expiry; control-plane disconnect immediately clears UDP credentials/endpoints.
- Maximum TOTAL UDP datagram <=1200 bytes, no fragmentation/reassembly in this version. Advertise conservative MaxUnreliablePayloadBytes = 900
  (or another reviewed constant if envelope overhead proves incompatible). Reject oversized data before send, never truncate/silent TCP fallback.
- Stats count application datagrams/bytes separately from keepalive/bind controls. LargestDatagramBytes counts actual wire size.
- SendUnreliableAsync retains caller memory only until its returned task completes. Runtime owns packet rentals until all fanout sends complete.
  Received callback memory is borrowed only until callback return; decode/copy before returning or awaiting. Async Relay forwarding owns its buffers.
- Bound queues/in-flight work; drop overloaded unreliable data instead of buffering old frames indefinitely. No business task per packet without bound.
- UnreliableEnabled is a diagnostics gate for application data only; keep TCP/control/UDP binding/keepalive alive. No game disconnect when off.
- Relay server has `public bool UdpForwardingEnabled { get; set; }` and extends existing RelayStatistics UDP fields. Pausing it drops ONLY gameplay
  datagrams, not reliable traffic or binding maintenance. Statistics expose loss/drop/bind counters.
- UDP is authenticated, not promised encrypted; reliable credentials must use TLS outside development. Don't mislabel HMAC as encryption.

## RPC/Weaver and buffer ownership

- Weaver validates Delivery enum and void-only Unreliable, emits it to bridge. All existing reliable signatures/IDs/proxies keep working.
- Canonical implementation + interface aliases store delivery and target metadata; receiver checks actual arrival lane and permissions before body.
- Runtime subscribes separately to IRoomDatagrams.UnreliableReceived. Do not feed UDP into reliable handler that returns ACKs/errors.
- `CallAsync<T>` remains reliable; reject attempts to call an Unreliable handler with a result. Unsupported lane absence is explicit RpcException;
  startup can wait readiness rather than silently failing over to TCP. Honor Host HoldBroadcasts / readiness barrier for UDP recipients too.
- Add support for `ArraySegment<T>` RPC parameters with supported element codecs; encode ONLY Offset..Offset+Count, not rented Array capacity.
  No Span/pointer public RPC and no SyncVar collections. Receiver may own decoded arrays; no promise of zero total allocations.
- Encode synchronously before a void call returns, so the caller may overwrite/reuse its Pose buffer immediately. Own a pooled byte buffer until
  all relevant send tasks complete. Broadcast encodes identical payload once where routing permits, not once per recipient.
- Use ArrayPool/IBufferWriter or equivalent pooled bounded packet writer. Record allocation measurements; don't claim zero GC if JSON/boxing persists.
- Expose `public int MeasureUnreliableCall(TargetKey key, MethodInfo method, object?[] args)` on RpcRuntime. It uses the SAME wire codec and returns
  exact encoded payload length with no sending. Arena uses it to split by encoded length vs MaxUnreliablePayloadBytes, not just number of records.
  It must allow measuring an over-budget candidate then reducing its batch, but cap absurd input sizes.

## Arena migration

- Contracts now have PlayerPose.MotionId, scalar structs PlayerMotionUpdate (Id,X,Z,AimX,AimZ) and BulletMotionUpdate (Id,X,Z).
- Assign compact stable MotionIds within current Scope and send mapping/name/PeerId/PlayerId ONLY through reliable roster/lifecycle/snapshot.
  Never repeat identity strings/Health in every UDP pose. A new spawn/lifetime gets a fresh ID; no name-based implicit resurrection.
- Replace periodic reliable PublishPoses with Unreliable RPC batches using ArraySegment<T> over reusable buffers. Add dictionary per-entity last
  pose Tick; out-of-order/duplicate batches cannot roll back, missing batch doesn't despawn anything. Unknown/removed IDs never spawn from UDP.
- Keep reliable roster on join/leave, bullet spawn/end, death/respawn lifecycle, Health SyncVar, input Move/Fire, initial full snapshot.
  Updates may cross channels: allow a bounded latest-pose buffer for a still-initializing known lifetime, or drop and recover next update.
- Authority samples into reused value-type motion arrays. Avoid Snapshot().Players.ToArray() as the network-send hot path. Full user GetView copies
  and reliable lifecycle snapshots may still allocate; report that separately. Quantify per-position-send allocations via diagnostics/tests.
- Batch by MeasureUnreliableCall byte size; refuse a single record that cannot fit. No reliable fallback. Same ArraySegment data remains valid at
  call entry and may be reused after synchronous encoding; never return pooled byte buffers while send still references them.
- Client startup waits UDP binding readiness before DirectoryReady, with a bounded timeout and explicit failure. Reliable readiness and UDP readiness
  are distinct diagnostics. If UDP disabled deliberately, skip/drop position lane while reliable input/state and member lifetime continue.
- IArenaSession now has GetDatagramReport, SetUdpEnabled, RebindUdpAsync. Populate ArenaReport.Datagrams and HUD; no secret fields.
- Add F4 to toggle application UDP locally, F5 to request client endpoint rebind if useful. Keep F2/F3. CLI `--udp-off-after <seconds>` and
  `--udp-on-after <seconds>` optional for automation; diagnostic query `QueryHostTick` reliable once/second increments ReliableProbesCompleted.
  Count actual received Health SyncVar changes. PositionTick only advances from UDP (initial snapshot may seed it once).
- Sample Relay exe `--udp-control-file <path>` polls admin-owned local file for `on`/`off` and sets UdpForwardingEnabled, enabling true process E2E
  channel isolation. It is NOT a remotely callable public endpoint. Reports existing UDP statistics.

## Acceptance

1. Existing reliable/direct/Relay suites pass; core stays netstandard2.1/C#9, transport builds both targets.
2. Actual TouchSocket UDP direct and relay: valid bind/data, bad MAC/token/scope/replay/wrong endpoint/oversize rejection, rebind, close cleanup.
3. REAL woven Unreliable Host/All/Target calls: reliable/UDP counters distinct, no ACK/pending/result, arrival Delivery spoofing rejected.
4. Oversized calls rejected, ArraySegment prefix only, source buffer reusable immediately with delayed wire send, pool returned after completion/fault.
5. Simulation drop/reorder test verifies positions/clock recover; generic events not collapsed as latest state.
6. Arena actual multi-process direct and forced Relay; UDP stats positive, datagrams <=1200, no position RPC bytes on TCP hot path.
7. Pause ONLY Relay UDP: PositionTick freezes, reliable probes and Health changes continue; resume UDP recovers; whole Relay loss still disconnects.
8. Rebind real Client UDP endpoint without replacing room Peer identity; old grants cannot reclaim endpoint. No Internet/NAT deployment claim from loopback.
9. Allocation report distinguishes reused motion-array count, per-send managed bytes, and presentation allocations. No zero-GC claim without evidence.
