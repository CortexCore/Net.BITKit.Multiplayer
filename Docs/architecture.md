# BITKit Multiplayer — current architecture

## One runtime and one backend

The supported backend is `BITKit.Multiplayer.NetRpc`: MessagePack RPC, native TCP+UDP Direct/Relay,
generated remote interfaces, woven ordinary classes, interface state and NetEntity components.
The B4/B5/B6 runtime, packet factory, room-wire abstractions, typed MemoryPack codecs and mixed weaver
have been removed. New work must not restore them or add a backend-selection attribute.

Core targets netstandard2.1 without UnityEngine references. Default asynchronous APIs use UniTask 2.5.10;
Task/ValueTask are supported interoperation boundaries. Core no longer depends on MemoryPack.

## Authoring and dependency injection

- `[Rpc(SendTo.Host)]`: Client sends to authority; a Host call executes locally.
- `[Rpc(SendTo.All)]`: only Host may initiate; Host and each attached Client execute once.
- All and Unreliable RPCs return void. Host RPCs support void, Task/ValueTask/UniTask and their result forms.
- Unsupported declarations produce build-time diagnostics rather than ordinary local execution.
- Ordinary RPC objects use standard `AddSingleton<T>()`; DI resolution runs woven constructor registration.
- The current weaver requires a source `IRpcContext<TContract>` constructor parameter and an explicit
  IDisposable/Dispose implementation. Removing this author constraint is a separate change.
- Interface-only Clients use `AddRemoteInterface<T>()`. `AddNetRpcService<TContract,TImplementation>`
  owns interface aliases, contract metadata and native receiver registration; it remains an active API.
- `AddNetRpcRuntime` creates an unattached runtime and supplies context/entity infrastructure.
  `AddNetRpc` additionally attaches an existing connection and starts Host state publishing.

## Rooms, authority and lifetime

Host and Client are exclusive roles; a player Host and a dedicated Host have the same authority semantics.
Host is not a hidden Host+Client pair. Each room owns its DI container, RpcContextService, connections,
targets, pending requests and state. Peer 1 is authority; Host assigns fresh nonzero Client IDs >= 2.

Sender identity is taken from attached connections. Application admission and authorization remain
separate from UDP endpoint proof. Cancellation/timeout ends waiting, not already-running business effects.
Callbacks and replies are scoped to the originating connection generation and room scope.

## Generation and weaving

`CodeGen --remote contracts.dll generated.cs` emits native interface proxies/receivers.
`CodeGen input.dll output.dll` weaves every eligible Rpc method into NetRpc; there is no alternate default.
Unity's NetRpcILPostProcessor invokes the same module transform and preserves safe portable symbols.
The .NET target transforms the original intermediate DLL on each build, never already-woven output.

Per-instance dispatch bindings belong to their explicit Runtime/context. Never route through a global
current-room or active-runtime singleton. Receiver entrypoints call original method bodies directly.

## Transport and state

`BITKit.Multiplayer.NetRpc.ITransport` exposes OnReceived, reliable ordered Send and unreliable SendFast.
Transport deals only in complete borrowed byte frames; it does not resolve business methods or entities.
Send completion ends the payload loan; receive memory is valid only until synchronous callback return.
Connection owners are disposed by the room; ITransportLifetime.Closed detaches peers and fails pending work.

TcpTransport combines TCP reliable frames and authenticated UDP samples. RelayEndpoint forwards frames
to one authority Host via RelayHostConnection. Client uses the same ConnectAsync API for Direct and Relay.
LiteNetLib Direct is an independent optional extension depending on Core, not the other way round.

Host publishes interface scalars, NetworkList/NetworkDictionary operations and identified NetEntity components.
Clients read local cached state and reject writes. Revisions/schema/fingerprints reject stale or invalid data;
snapshots repair gaps. Transient RPCs are not replayed for late join. NetRpc has no legacy SyncVar/Hook weave.

## Unity boundary

The independent `Net.BITKit.Multiplayer.Unity` package supplies UnityNetRpcAdapter, the main-thread dispatcher,
NetworkIdentity/SceneIdentity and UnityNetworkObjects. Adapter.Pump performs main-thread ingress and publishing.
Object roster lifecycle is separate from continuous NetComponent state; Identity alone does not sync Transform.

See [README](../README.md), [API contracts](api-contracts.md), [current status](current-status.md)
and [legacy removal evidence](legacy-backend-removal.md). Historical pages are dated evidence, not supported APIs.
