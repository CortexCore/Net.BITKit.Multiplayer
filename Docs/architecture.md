# BITKit Multiplayer — architecture contract

**2026-10-03 新设计入口：** 用户手写 design-v1/design-v2 已在独立 NetRpc backend 落地；DI、MessagePack、IL Wrapper、TCP+UDP/Relay、ECS/接口状态见 [新链路](design-implementation.md)。本页随后描述的 B6、MemoryPack、TouchSocket、旧 Unity 接入属于既有 backend 的兼容架构，不覆盖新字节 Transport 方向。

Status: current architecture contract; Unity adapter is the next implementation phase.
Architecture owner: Astra. Implementation: Sol. Source/document review: 2026-09-28.

## 1. Purpose and boundaries

Implement a pure .NET, DI/scope-oriented multiplayer runtime. Network identity is not a GameObject.
Existing Project B backend (accounts/store/currency) remains unchanged and keeps its remote service contracts.
The current v1 direction is a pure self-owned NetRpc/INetProvider runtime. TouchSocket is a retained
legacy adapter for existing special services, not the game RPC authority. The first v1 packet I/O
implementation is a pure .NET TCP listener transport; UDP and other high-throughput transports are
subsequent implementations of the same byte-channel seam. Reliable and unreliable calls are explicit
policies over the self-owned provider and must not silently change each other.

Normal woven calls on BOTH deliveries use typed B6/v4 entrypoints/receivers, numeric IDs and schema
fingerprints, borrowed-memory room wires and pooled message ownership. Void is one-way; Task/Task<T>
retain request/reply. Reliable delivery does not imply a void success ACK/waiter. B5/v3 remains for
control/state/replies and explicit reflection APIs; B4/v2 is an explicit legacy UDP compatibility path.
There is no Runtime JSON/JToken fallback. Generated reliable DTOs have explicit member order and
bounded supported MemoryPack schemas. Dynamic Host catalog exchange is deferred; fingerprints are
checked against local generated descriptors on receipt. See [typed-rpc-guide.md](typed-rpc-guide.md)
and [reliable-binary-guide.md](reliable-binary-guide.md). Disk diagnostics and Lobby APIs are separate boundaries.

Two authoring surfaces converge on one runtime:

1. Remote service contracts (`Task<T>`), whose implementation may exist only on the remote endpoint.
2. Ordinary implementation objects with woven `[Rpc(SendTo.Host|All|Target)]` methods and `[SyncVar]` properties.

Do not require NetworkBehaviour, MonoBehaviour, virtual methods, engine objects, or Unity assemblies in core.

## 2. Role and identity

- `NetworkRole`: Offline, Host, Client. Host and Client are mutually exclusive.
- A player-host and a dedicated host have the SAME Host role. All includes either kind of Host exactly once.
- `INetworkContext`: read-only Role, IsHost, IsClient, LocalPeerId, HostPeerId, IsConnected, IsReady, room/session identity.
- A PeerId is a logical connection/session identifier, NOT SteamId, account ID, entity ID or socket address.
- A room member directory is authority-owned; maps PeerId to optional existing player/account ID and SteamId plus readiness.
- Peer IDs must not be silently recycled into pending calls. Connection/room epochs reject stale delivery.
- Targets use room/session scope + stable service key; entity-scoped targets additionally include NetworkId/component key.
- Scope identity must accompany every request/reply/event/state update and be checked on receipt.
- Multiple rooms and host/client runtimes must coexist in one process with no target or role leakage.
- An AsyncLocal call context is permitted for sender metadata, always stack-restored, including async exception paths.

## 3. Public semantics

```csharp
public interface IInventoryService { Task<ItemList> GetListAsync(); }
public sealed class InventoryService : IInventoryService
{
    [Rpc(SendTo.Host)]
    public Task<ItemList> GetListAsync() { /* authority body */ }

    [Rpc(SendTo.All)]
    public void Notify(string message) { /* runs on Host and ready peers */ }

    [Rpc(SendTo.Target)]
    public Task<ItemList> ReadLocalAsync(RpcTarget target) { /* executes on selected peer */ }

    [SyncVar]
    public int Version { get; private set; }
}
```

- The author supplies real method bodies. Woven concrete AND interface calls must route identically.
- Attributes live on implementations for woven objects; interface-only remote proxies use contract metadata/configuration.
- No annotated implementation on the caller is required for a remote service interface proxy.
- Host: callers send to room authority; when already Host, execute the body locally, respecting call context.
- All: only Host can initiate; each ready peer and the Host executes once. No synthetic local-client role.
- Target: dynamically choose exactly one peer, including self or Host. Client-to-client routes through Host initially.
- RpcTarget is routing metadata, excluded from ordinary serialized business arguments. All rejects a result type.
- Return support: `void`, `Task`, `Task<T>` (Host/Target). `Task<T>` returns the designated execution result.
- Core never references UniTask. Provide extension/adaptation boundary and document Unity UniTask work honestly.
- `[HostOnly]` / `[ClientOnly]`: local guards, no sending. Wrong-role use fails explicitly, not silent skipped business writes.
- RPC events are transient; late join does NOT replay past All events. Durable data uses state synchronization.
- Unbound/disposed instance calls fail explicitly. No routing by “last active runtime”.

## 4. Permission and invocation

- Direction/target is separate from permission. Sender identity comes from transport/session binding, never caller-supplied fields.
- Relaying must preserve original sender while authenticating that only the Host may relay.
- Entity-scoped Host calls default to owner-only. Service-scoped calls require explicit policy/authorization registration.
- Runtime passes requester PeerId/player info/target/context to authorization. Host local invocation uses Host identity.
- Target calls also pass authorization at the final peer; the Host cannot accidentally impersonate an originating client.
- Calling a receiver body must not resend the same RPC. A nested DIFFERENT RPC must still send normally.
- Async receive suppression must not be an unbounded/global “network disabled” switch. Exact invocation identity and lifetime matter.
- No automatic retries of side-effecting calls. Timeout means result unknown, not guaranteed non-execution.
- Cancellation cancels local waiting; does not promise rollback of already-running remote business logic.
- Disconnect, unbind or scope disposal completes/fails pending operations; nothing waits forever.
- Report unauthorized, missing target/method, invalid payload/version, remote fault, timeout, cancellation, disconnected distinctly.
- Use versioned stable wire contract identifiers, bounds, and explicit serializable DTOs. Never deserialize arbitrary CLR type names.
- Methods may overload if wire IDs unambiguously include signature; duplicated identities must fail.

## 5. SyncVar v1

- Ordinary properties only: primitives, enums, strings, explicitly supported value DTOs. Host authority.
- Prefer auto-property setter weaving; detect/reject unsupported property shapes instead of a silent missed update.
- Host setter writes produce changes only when value changes. Client local writes fail. Receive apply bypasses publish, scoped.
- Full state for newly ready peers, versioned updates thereafter; stale snapshots/deltas cannot roll state back.
- Preserve state pending target bind; bound/scoped objects consume current state on bind. Bound buffers and clear on leave.
- Emit change notifications after successful apply; define initial snapshot notification behavior.
- Object/DI identity preserved; don't reconstruct service providers on received values.
- No ordinary List/Dictionary mutation interception or automatic deep graph observation. Explicitly initialized getter-only SyncList, SyncDictionary and SyncHashSet now provide Host-authoritative bounded incremental state.
- Collection batches are atomic per collection; fingerprinted snapshots/deltas use version recovery and scoped ownership. Scalar and collection Hooks execute outside bookkeeping locks. See [the implemented collection contract](sync-collections-guide.md).

## 6. Layering and DI

- `Src/Runtime`: contracts + runtime, netstandard2.1, root namespace BITKit.Multiplayer.
- `Src/Transport`: native packet I/O + factory registration; separate runtime assembly, no Unity dependency.
- `Src/TouchSocket`: retained legacy/special-service adapter. It is not the v1 game RPC runtime and new NetRpc services must not depend on it.
- `CodeGen`: Cecil transformation library/tool, build-time dependency only.
- `Projects/*.csproj`: compile shared source explicitly; all output outside Src.
- `Src/package.json`: UPM package identity placeholder. Tests/tools stay outside Src; Runtime/Transport/TouchSocket asmdefs and Unity dependencies still need the adapter-phase work recorded in current-status.
- `Tests`: modern .NET runner, actual fixture compilation + weaving before invocation, socket integration tests.
- `Samples/ConsoleRoom`: hand-authored RPCs, readme/run command, no Unity or business-server dependency.
- Microsoft.Extensions.DependencyInjection integration resolves interface+concrete alias to same instance and binds it to scope.
- Each RpcRuntime holds transport, targets, member directory, pending requests, authorization, state. No static mutable transport.
- `NetRpcV1.cs` is the first pure .NET prototype of the original INetProvider shape: `NetRpcModel` carries target/method/request/count/payload, `RpcContextService` owns DI dispatch and pending requests, `ITransport` exposes only received bytes and send bytes, and `TcpTransport` supplies the first length-prefixed listener/client implementation. v1 intentionally uses reflection and reusable argument bags before source-generated delegates replace the dispatch hot path.
- A weak object→binding lookup MAY exist as a dispatch bridge if it stores only explicit per-object runtime bindings and removes
  deterministically on unbind; it must not route by global active context. Prefer bound field/interface introduced by weaving.
- Scheduler abstraction may marshal Unity access later; core is thread-safe and can test on normal .NET execution.
- A room may attach multiple transport endpoints at once. Direct, Relay, Replay and Bot are transport kinds, not game roles or room modes. `RoomTransportHub` routes PeerId-bound traffic through the selected endpoint; virtual Replay/Bot transports use the same ingress/egress contracts as socket transports. Host creation never depends on account, matchmaking or Relay availability.

## 7. Weaver

Use Mono.Cecil with a standalone .NET transformation/test path first. Keep method identity/call sites correct.
Generate/rewrite entrypoints and receive bodies without executing arbitrary reflected remote methods.
Preserve exception handlers, async state-machine behavior and debug symbols where applicable; diagnose unsupported shapes.
Prevent repeated weaving. Do not weave dependency libraries or compiler-generated code indiscriminately.
Build-time rejection covers invalid return shapes, generic/open targets if unsupported, ref/out, async void, async All returns,
unsupported routing parameters, ordinary/nested collection schemas, replaceable sync containers, invalid Hooks, missing scalar setter, duplicate contracts and conflicting attributes.
Compatibility with Unity ILPP is an adapter concern; do not port current global static Unity prototype into core.

## 8. Required acceptance slices

1. dotnet build solution succeeds; core builds netstandard2.1; tests run on modern .NET.
2. Same-process two rooms do not cross-route even when service/object keys coincide.
3. REAL woven ordinary class through both interface and concrete calls; Host/All/Target correct.
4. All includes Host once including dedicated topology; Client cannot initiate All.
5. Task<T> result, Task completion, void dispatch, remote failure, cancellation/timeout/disconnect/disposal.
6. Client A→Host relay→Client B returns only to A, correct original sender/permissions, no impersonation.
7. A remote-interface-only client invokes a Host implementation without owning its implementation type.
8. SyncVar assignment, unchanged suppression, local Client denial, snapshots/late bind/late join, scope cleanup.
9. Unsupported declarations diagnosed by actual codegen tests, not just validation helper mocks.
10. Real TouchSocket TCP/DMTP loopback, not only fake transport. No Unity Play required.
11. Console sample and truthful limitations. No business-server modifications, no commits/pushes.

## 9. Initial Unity migration boundary

This phase must not alter the dirty Project B Unity codebase's old weaver or business-server assemblies.
Existing prototype has static binding, Command naming and incomplete transport: it is NOT production architecture.
After pure .NET acceptance, plan a separate Unity ILPP/UniTask/main-thread adapter and replace old entrypoints deliberately.
UPM metadata alone does not prove Unity/AOT/build compatibility. State exactly what is not Unity-verified.
