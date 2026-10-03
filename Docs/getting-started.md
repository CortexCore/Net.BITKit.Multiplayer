# 快速开始：当前 .NET 用法

这是独立库用法。Unity 当前宿主可直接使用 [Edit Mode 双窗口](unity-editor-probe.md)；剩余接入边界见 [当前状态](current-status.md) 和 [接入手册](unity-integration-plan.md)。

## 最快看到联动：Arena

在仓库根运行 `Start-Arena.cmd`，或：

```powershell
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -DedicatedHost -Clients 2 -Transport native
```

默认使用 native UDP + TouchSocket 可靠会话。玩家 Host 默认 Relay；Dedicated Host 默认 Direct。WASD/鼠标操作，F2 切视图、F3 切插值、F4 切本地 gameplay UDP、F5 Client 重绑。日志在 `Artifacts/ArenaRuns/`。图形操作与参数的完整说明位于仓库 `Samples/Arena/README.md`。

Arena Game 的 Build target 自动从未编织中间 DLL 生成 typed wrapper/receiver；不要对其输出再手动运行一次 Weaver。

## Build once, weave the application, then run

Use .NET 10 SDK for the tool, sample and tests. Consumers of the shared core reference
`Projects/BITKit.Multiplayer.csproj` (assembly `Net.BITKit.Multiplayer`, namespace `BITKit.Multiplayer`).
The native packet implementation is `Projects/BITKit.Multiplayer.Transport.csproj`; reliable room/session and Relay adapters are `Projects/BITKit.Multiplayer.TouchSocket.csproj`.

```shell
dotnet build Net.BITKit.Multiplayer.slnx -t:Rebuild
dotnet test Net.BITKit.Multiplayer.slnx --no-build
dotnet run --no-build --project CodeGen/BITKit.Multiplayer.CodeGen.csproj -- Artifacts/bin/ConsoleRoom/Debug/net10.0/ConsoleRoom.dll Artifacts/bin/ConsoleRoom/Debug/net10.0/ConsoleRoom.dll
dotnet run --no-build --project Samples/ConsoleRoom/ConsoleRoom.csproj
```

The sample uses real loopback TCP/DMTP and prints `Host result: 42`. The two runtime instances
are independent Host and Client peers. It does not turn a Host into a synthetic local Client.
Rebuild the sample before weaving it again; the weaver rejects repeat processing. Production
applications should put this transform into their deployment/build pipeline before assembly loading.

## Hand-authored implementation

```csharp
using BITKit.Multiplayer;
using System.Threading.Tasks;

public interface IRoundService
{
    Task<int> GetRoundAsync();
}

public sealed class RoundService : IRoundService
{
    [SyncVar]
    public int Round { get; private set; } = 1;

    [Rpc(SendTo.Host)]
    public Task<int> GetRoundAsync() => Task.FromResult(Round);

    [Rpc(SendTo.Target)]
    public Task<string> DescribeLocalAsync(RpcTarget target)
    {
        if (!RpcCallContext.TryGetValue(out var call))
            throw new System.InvalidOperationException("Missing RPC context");
        return Task.FromResult("Executed by " + call.Executor);
    }

    [Rpc(SendTo.All)]
    public void AnnounceRound(int round)
    {
        // Execute once per ready peer, including a dedicated Host.
    }

    [HostOnly]
    public void Advance() => Round++;
}
```

Weave the implementation assembly. Bind a separate implementation instance at the same TargetKey
on each runtime that should execute it:

```csharp
var key = new TargetKey("round/v1");
var service = new RoundService();
host.Bind(key, service, authorize: request => true); // Sample permits all room members; choose an application policy.
```

Calls through `RoundService` and `IRoundService` both use the woven entrypoint. Unbound RPC calls
fail explicitly. Constructor SyncVar initialization before first bind is local; after binding,
only the Host may write, and received Client state does not trigger another publish.

普通 woven 调用在 Reliable/Unreliable 上均使用 B6/v4 强类型路径。`void` 是单向通知，没有成功回复或结果等待器；`Task`/`Task<T>` 才等待远端完成。要发送 UDP，在 void 方法上添加 `Delivery = RpcDelivery.Unreliable`；接收实现与 schema 必须匹配。[API 语义](api-contracts.md) 有完整边界。

When only the interface exists at the caller, no implementation instance is required. This explicit proxy is a **reflection/DispatchProxy compatibility path**, not the generated zero-allocation/AOT path:

```csharp
var routes = new Dictionary<string, SendTo>
{
    [RpcRuntime.MethodId(typeof(IRoundService).GetMethod(nameof(IRoundService.GetRoundAsync))!)] = SendTo.Host
};
IRoundService remote = client.CreateProxy<IRoundService>(key, routes);
int round = await remote.GetRoundAsync();
```

The receiver checks the implementation's declared route, so the caller cannot change an All
method into a Host method by configuring a different proxy route.

## DI scope and identities

Register one `RpcRuntime` per room scope. Register `INetworkContext` as an alias to that runtime.
`AddScopedRpcService<TContract,TImplementation>(key, authorize, owner)` aliases interface and
implementation to one scoped instance and binds it. Dispose the runtime with the scope.
Business services should inject INetworkContext to read IsHost/IsClient; RpcCallContext is invocation
metadata, not a global current runtime. Prefer TryGetValue in synchronous hot handlers; Current is the
class compatibility view. Nested async contexts are handled separately.

DI 组装可以遵循这个函数（runtimeFactory 由已认证会话层提供，每个 room scope 返回新的 Runtime）：

```csharp
using System;
using BITKit.Multiplayer;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;

static ServiceProvider CreateRoomServices(
    Func<IServiceProvider, RpcRuntime> runtimeFactory, PeerId owner)
{
    var services = new ServiceCollection();
    services.AddUdpTransport();
    services.AddScoped<RpcRuntime>(runtimeFactory);
    services.AddScoped<INetworkContext>(p => p.GetRequiredService<RpcRuntime>());
    services.AddScopedRpcService<IRoundService, RoundService>(
        new TargetKey("round/v1"), owner: owner);
    return services.BuildServiceProvider();
}
```

应用随后 `CreateScope()`，从该 scope 解析 RoundService/IRoundService；它们是同一个对象。不要在多个 scope 的 factory 中返回同一个 Runtime 或 wire。Runtime.Dispose 会调用所持 wire.Dispose；需要严格等待底层关闭时继续遵守 endpoint 的 Completion/StopAsync 契约。

Both room scope and PeerId must identify a fresh session. A PeerId is not a SteamId or an Entity ID.
Host-only `RegisterMember` publishes a directory containing optional PlayerId/SteamId and readiness.
Use `TryGetReadyPeerBySteamId` or `TryGetReadyPeerByPlayerId` before building `new RpcTarget(peer)`.
Client-to-client Target calls relay through Host; original requester identity is retained.
For a peer-only target with no Host implementation, explicitly `RegisterRelayPolicy` on Host.

TouchSocket admission has two separate steps:

1. The application authenticates the connection and calls `hostWire.Admit(peer, sessionId)`.
2. Host calls `RegisterMember`; after a real application admission/ready acknowledgment, Client calls `ConfirmReady()`.

Wait for `client.IsReady` (or observe `MembersChanged`) before issuing RPCs: `ConfirmReady()` records local
confirmation and requests the directory, but the authoritative ready membership still has to arrive over the network.

The DMTP verify token is not account authentication. Existing business-server authentication can
supply the admitted identity without changing its RPC contracts. `RequestMembers()` lets a ready
client repair its directory explicitly.

Host readiness demotion also reaches the demoted peer: it remains connected but cannot make RPCs.
Host re-ready restores it. Host removal sends a terminal revocation and the Client becomes logically
disconnected even if its physical socket is still open; dispose the old runtime/wire and authenticate
a fresh PeerId for a replacement connection. Stale directory packets cannot reverse revocation.

## State and lifecycle

- Scalar SyncVar auto-properties and explicitly initialized getter-only SyncList/SyncDictionary/SyncHashSet; no fields, ordinary List/Dictionary observation or deep graph mutation. See [collections and Hooks](sync-collections-guide.md), including the independently runnable `Samples/SyncCollections` demo.
- Host snapshots current state to new authorized ready peers; new bindings apply buffered latest values.
- `StateChanged` occurs only when the applied value changes, including a differing initial snapshot.
- Authorization method key `$sync.state` governs state visibility separately from RPC names.
- `Unbind(key)` releases an instance but retains state for rebinding the same logical target.
- `RemoveTarget(key)` tombstones the target for the current scope at every admitted peer (including RPC-only targets);
  new directory snapshots carry the tombstone for late joiners. That target key cannot be reused in the same scope.
- Scope disposal/disconnect completes waiting calls. A timeout/cancellation cannot undo business work already executing.
- Remote void handler faults are receiver-side `UnhandledDispatch` diagnostics, not sender-correlated replies. Local validation still throws; asynchronous send faults are local diagnostics.

## Current limits and deferred integration

Task and Task<T> work on Host/Target. All accepts void only. CancellationToken is available through
explicit `CallAsync<T>`; woven CancellationToken parameters and UniTask adaptation are not implemented.
Generic/open implementation classes/methods, ref/out, async void, repeated weaving and unsupported
state shapes are rejected. Explicit/inherited interfaces, overloads, nested RPCs, and ordinary
branch/exception-handler/async fault paths are covered by tests.

CodeGen currently drops debug symbols. Interface proxies use DispatchProxy/reflection; IL2CPP/AOT
and trimming need a separate generated-registration/proxy adaptation pass. Unity ILPP, UniTask,
main-thread scheduling and Project B prototype migration remain a separate phase. Do not install
the UPM directory expecting its dependencies and weaver to configure themselves.
