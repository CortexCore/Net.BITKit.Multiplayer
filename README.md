# BITKit Multiplayer

面向 **Unity 和 .NET / .NET Core** 的通用网络模块：用普通 C# 类和接口编写 RPC，用 Host 权威状态驱动对象同步，并通过可替换的传输层连接不同运行环境。

核心运行时基于 `netstandard2.1`，不依赖 UnityEngine；默认异步 API 使用 UniTask。内置 TCP + UDP Direct 和 Relay，Unity 对象适配、LiteNetLib 传输分别位于独立扩展仓库。

## 1. 使用 RPC

在普通实例方法上标记 `[Rpc]`，注册并解析对象后，按普通方法的方式调用：

```csharp
using System;
using BITKit.Multiplayer;
using BITKit.Multiplayer.NetRpc;
using Cysharp.Threading.Tasks;

public sealed class PlayerRpc : IDisposable
{
    private int health = 100;

    // 编织器使用注入的上下文，将这个实例接入当前房间。
    public PlayerRpc(IRpcContext<PlayerRpc> rpcContext) { }

    [Rpc(SendTo.Host)]
    public UniTask<int> Damage(int amount)
    {
        health = Math.Max(0, health - amount);
        return UniTask.FromResult(health);
    }

    [Rpc(SendTo.All, RpcDelivery.Unreliable)]
    public void Pose(uint entityId, float x, float y, float z)
    {
        // 在本端消费位置样本，更新角色表现。
    }

    public void Dispose()
    {
        // 释放本对象持有的业务资源；DI 容器同时释放注入的 RPC 上下文。
    }
}
```

```csharp
// Client 发起，方法体在 Host 执行；等待 Host 返回结果。
int health = await clientPlayer.Damage(10);

// Host 发起，Host 和各个 Client 分别执行一次。
hostPlayer.Pose(entityId: 7, x: 1, y: 0, z: 2);
```

普通类 RPC 使用标准 `AddSingleton<PlayerRpc>()` 注册到房间 DI 容器，并从容器解析实例。当前编织器仍要求构造函数参数 `IRpcContext<T>`、实现 `IDisposable` 并声明 `Dispose()`；编织后的构造函数负责接入当前房间，业务无需额外调用对象注册 API。

RPC 由构建期编织器生成发送包装和接收入口。**`.NET` 工程需启用 `NetRpcWeave` 并导入 `Tools/NetRpc/NetRpc.targets`；Unity 的 ILPostProcessor 自动处理 `[Rpc]`，无需额外后端标记。** 两端都只有 NetRpc 一套实现。完整工程配置见 [NetRpc.Sample.csproj](Samples/NetRpc/NetRpc.Sample.csproj)。

### `SendTo` 的语义

| 路由 | 调用方 | 执行位置 |
| --- | --- | --- |
| `SendTo.Host` | Client 或 Host | Client 调用时发送到 Host；Host 调用时直接执行本地方法体 |
| `SendTo.All` | Host | Host 本地一次，以及当前连接的每个 Client 各一次 |

Host 是权威端，可以是独立服务器，也可以由玩家进程承担；它不会隐式创建一个本地 Client。Client 调用 `SendTo.All` 会被拒绝，需要广播的业务应先通过 Host RPC 提交给 Host。

当前路由支持 `Host`、`All`。

- `void`：单向调用，不等待远端业务完成，也没有业务成功回复。
- `UniTask` / `UniTask<T>`：等待远端完成或返回结果；`Task` / `ValueTask` 也支持作为兼容返回类型。
- `SendTo.All` 和不可靠 RPC 必须返回 `void`。

### 通过接口调用

服务也可以只向 Client 暴露共享接口：

```csharp
public interface ICalculator
{
    UniTask<int> Plus(int a, int b);
}

public sealed class Calculator : ICalculator
{
    public UniTask<int> Plus(int a, int b) => UniTask.FromResult(a + b);
}
```

Host 注册 `AddNetRpcService<ICalculator, Calculator>()`，Client 注册 `AddRemoteInterface<ICalculator>()`，业务从 DI 获取 `ICalculator` 并直接 `await calculator.Plus(20, 22)`。接口方法默认调用 Host；Client 不需要 Host 的实现类。

共享接口代理通过构建期生成，`.NET` 配置 `NetRpcContractsAssembly`；完整接口例子见 [Samples/NetRpc](Samples/NetRpc)。

## 2. 不可靠通道用来做什么

默认 RPC 使用可靠通道。内置传输的可靠通道走 **TCP**，不可靠通道走 **UDP**；其他传输可以使用自己的对应通道。

不可靠通道适合持续产生、允许丢失、能被新样本覆盖的信息，例如：

- 角色位置、朝向、瞄准方向。
- 高频移动输入或临时表现事件。
- 网络组件的完整状态样本。

它不重传旧消息，也不等待确认，可以避免旧状态因重传而拖延新状态。接收端可以按业务 tick 丢弃过期样本，并进行插值。库存操作、交易结算等需要明确完成结果的业务使用可靠 RPC。

```csharp
[Rpc(SendTo.All, RpcDelivery.Unreliable)]
public void Pose(uint entityId, float x, float y, float z) { /* 应用样本 */ }
```

**不可靠 RPC 不等于持久状态同步。** 丢失的 RPC 不会自动重放；持续状态通过后续样本、组件快照和周期性全量同步收敛。不可靠帧还需控制在传输的单包上限内，超限不会自动改走可靠通道。

## 3. 开启 Host 和连接 Client

以下示例分别运行在 Host、Client 的异步会话入口中；`cancellationToken` 控制会话退出。使用前面的 `PlayerRpc`，两端使用相同的房间 `scope`。

```csharp
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
```

### Host

先创建房间运行时，再监听并接入 Client：

```csharp
using var host = new ServiceCollection()
    .AddSingleton<PlayerRpc>()
    .AddNetRpcRuntime(isServer: true, scope: 42)
    .BuildServiceProvider();

var runtime = host.GetRequiredService<RpcContextService>();
var player = host.GetRequiredService<PlayerRpc>();
runtime.StartSynchronization(new NetRpcOptions());

using var listener = new TcpTransportListener(
    new IPEndPoint(IPAddress.Any, 7777));

// 最小示例接入一个 Client；2 是 Host 分配的逻辑 Peer ID。
await using var transport = await listener.AcceptAsync(cancellationToken);
runtime.AttachPeer(2, transport);

await Task.Delay(Timeout.Infinite, cancellationToken);
```

多 Client 房间循环调用 `AcceptAsync`，为每次连接分配新的逻辑 Peer ID，并用同一个 Host Runtime 调用 `AttachPeer`。`1` 保留给 Host，Client ID 从 `2` 开始；连接由房间会话保存和释放。

### Client

```csharp
await using var transport = await TcpTransport.ConnectAsync(
    "127.0.0.1", 7777, cancellationToken: cancellationToken);

using var client = new ServiceCollection()
    .AddSingleton<PlayerRpc>()
    .AddNetRpc(isServer: false, transport: _ => transport, scope: 42)
    .BuildServiceProvider();

var player = client.GetRequiredService<PlayerRpc>();
int health = await player.Damage(10);

// 容器和连接保持存活，继续接收 Host RPC 与状态更新。
await Task.Delay(Timeout.Infinite, cancellationToken);
```

`AddNetRpc` 用于初始化一个已连接的 Transport；`AddNetRpcRuntime` 用于先建立运行时，再接入多个连接。每个房间拥有自己的 DI 容器和 Runtime，房间结束时释放它们及所有连接。

## 4. 使用 Relay 模式

Relay 负责转发连接，**业务仍由 Host 执行**。Host 和 Client 都主动连接可访问的 Relay 地址，Client 无需直接访问 Host 的监听地址。

```text
Client ── TCP / UDP ── Relay ── TCP / UDP ── Host
```

### 启动 Relay 服务

```csharp
await using var relay = new RelayEndpoint(
    new IPEndPoint(IPAddress.Any, 8888),
    hostKey: "room-host-key");

await Task.Delay(Timeout.Infinite, cancellationToken);
```

### Host 注册到 Relay

在已经创建的 Host `RpcContextService` 上挂接 Relay 连接，不需要先接入 Direct Client：

```csharp
await using var relayLink = new RelayHostConnection(
    runtime, "relay.example.com", 8888, hostKey: "room-host-key");

while (!relayLink.IsConnected)
    await Task.Delay(20, cancellationToken);

// relayLink 随 Host 房间保持存活。
```

Relay 和 Host 使用相同的 `hostKey`。这个侧链放在 Direct 接受连接之前，或用于只有 Relay 的 Host；也可以同时保留 Direct listener，两种入口共用同一房间运行时。Relay 连接断开后，Host 侧链会尝试重连。

### Client 连接 Relay

Client 使用同一个连接 API，把地址换成 Relay：

```csharp
await using var transport = await TcpTransport.ConnectAsync(
    "relay.example.com", 8888, cancellationToken: cancellationToken);
```

其余 Client DI 和 RPC 调用方式与 Direct 相同。每个 `RelayEndpoint` 对应一个权威 Host / 房间；`hostKey` 用于 Host 注册，玩家账号与业务权限由应用自己的准入流程处理。

## 5. 基本原理

```text
普通 C# 方法 / 共享接口
          ↓ 构建期生成、IL 编织
发送包装 → RpcContextService → ITransport
                                  ↓
业务方法 ← 生成的接收入口 ← 对端 Runtime
```

- **生成与编织**：接口生成代理，普通类 RPC 生成发送包装和直接调用原方法体的接收入口，业务调用保持普通 C# 写法。
- **协议与路由**：参数使用 MessagePack 序列化；数据帧携带房间 scope、目标 ID、方法 ID 和请求 ID。Runtime 负责路由、权限检查、请求结果及异常处理。
- **状态同步**：接口标量和网络集合保存 Client 本地缓存；带 `INetworkIdentity` 的 `NetEntity` 同步其 `INetComponent`。Host 修改权威状态，Client 消费更新。
- **状态收敛**：版本与指纹用于检查变化和旧消息；集合使用快照 / 增量，组件使用完整状态样本，并通过周期性全量同步修复丢失的更新。
- **房间隔离**：连接、对象注册和同步生命周期属于各自的 Runtime / DI 容器。Unity 适配层负责把接收处理和状态发布接回主线程。

## 6. 自定义 `ITransport` 扩展网络模块

实现 `BITKit.Multiplayer.NetRpc.ITransport`，就可以接入其他网络库或平台连接：

```csharp
public interface ITransport
{
    event Action<ReadOnlyMemory<byte>>? OnReceived;

    UniTask Send(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    UniTask SendFast(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}
```

- `Send` 对应可靠、有序通道；`SendFast` 对应不可靠通道。
- `OnReceived` 每次交付一个完整网络帧；流式传输的拆包和组包由扩展处理。
- 发送 UniTask 完成后，上层才能结束 payload 的借用。接收内存只在同步回调返回前有效，异步保留前需要复制。
- Transport 只负责字节和连接，不需要理解 RPC 方法、对象类型或状态协议。

连接有关闭事件时，同时实现 `ITransportLifetime`，通过 `Closed` 通知 Runtime；连接资源由会话持有，退出时释放。没有不可靠通道的扩展应明确拒绝 `SendFast`，避免悄悄改变通道语义。

接入自己的 Transport 实例：

```csharp
services.AddNetRpc(isServer, _ => myTransport, scope: 42);

// 或由已有 Runtime 接入；Host 为每个 Client 分配独立 Peer ID。
runtime.AttachPeer(peerId, myTransport);
```

[LiteNetLib 扩展](https://github.com/CortexCore/Net.BITKit.Multiplayer.LiteNetLib) 就是这种独立适配器：扩展依赖主库和 LiteNetLib，主库不反向依赖它。它的 `Src/` 是 Unity 包根目录，根目录 `.csproj` 给 .NET 使用。

## 7. Unity：适配 NetObject 与对象状态同步

Unity 适配位于 [Net.BITKit.Multiplayer.Unity](https://github.com/CortexCore/Net.BITKit.Multiplayer.Unity)。UPM Git 包地址为：

```text
https://github.com/CortexCore/Net.BITKit.Multiplayer.git?path=/Src
https://github.com/CortexCore/Net.BITKit.Multiplayer.Unity.git?path=/Src
```

宿主需提供 UniTask、MessagePack 和 DI 等依赖，具体配置见 [Unity 接入说明](Docs/unity-integration-plan.md)。

### 对象身份与生命周期

- **动态 Prefab**：挂载 Unity 扩展的 `NetworkIdentity`，填写 `PrefabAddress`；实现 `INetworkPrefabLoader`，让 Client 按地址加载并释放实例。
- **场景对象**：挂载 `SceneIdentity`，各端加载相同场景；由场景键匹配已有对象。
- **对象管理**：`UnityNetworkObjects` 负责 Host 分配 Entity ID、初始对象快照、生成 / 销毁及 Owner 变更。

创建 Unity 房间 Runtime 时使用 `AddNetRpcRuntime`，再通过 Adapter 接入连接和对象世界：

```csharp
using BITKit.Multiplayer.Unity;
using UnityEngine;

var adapter = new UnityNetRpcAdapter(runtime);

// transport 为已经建立的 TcpTransport；本段在 Unity 主线程执行。
adapter.AttachPeer(peerId, transport, transport, transport);
var objects = adapter.AttachNetworkObjects(
    worldGeneration: 1, localPeerId: localPeerId, prefabLoader: prefabLoader);

if (runtime.IsServer)
{
    // Host：生成网络 Prefab。
    var handle = await objects.SpawnAsync(
        prefab, position, rotation, ownerPeerId: playerPeerId);
}
else
{
    // Client：对象世界初始化后，拉取 Host 的当前对象快照。
    await objects.SynchronizeAsync();
}
```

Host 和 Client 分别执行自己的步骤；Host 的 `localPeerId` 为 `1`，Client 使用准入流程分配的逻辑 ID。Adapter 挂接时会注册当前已加载的场景对象，后来加载的对象调用 `RegisterSceneObject`。

每帧在主线程调用 `adapter.Pump(Time.unscaledTimeAsDouble)`，处理接收队列并发布 Host 状态。Owner 变化使用 `objects.SetOwner`，销毁使用 `objects.DespawnAsync`。

### 将对象的业务状态接入同步

对象生成、归属和初始位置属于生命周期同步；生命值、持续位置等状态通过 `NetEntity` 和 `NetComponent<T>` 接入。下面以一个已绑定的对象句柄 `handle` 为例，两端使用相同的组件 ID：

```csharp
var health = new NetComponent<int>(componentId: 1, initialValue: 100);
var entityServices = new ServiceCollection()
    .AddSingleton<BITKit.Multiplayer.NetRpc.INetworkIdentity>(handle.Identity)
    .AddSingleton<INetComponent>(health)
    .BuildServiceProvider();

var entity = new NetEntity(entityServices);
roomServices.GetRequiredService<IEntitiesService>().Register(entity);

health.Changed += (_, value) => { /* 将 value 应用到 GameObject / UI */ };

// 只有 Host 写权威状态；Client 通过 Host RPC 请求修改。
if (runtime.IsServer)
    health.Value = 80;
```

动态对象可在 `Initializing` 回调中建立 Entity 和组件；场景对象可在 `Spawned` 时接线，已存在的句柄从 `Objects` 枚举。位置同步同样可使用自己的位置状态组件，在 `Changed` 或每帧表现层中应用 / 插值到 Transform；挂上 Identity 本身不会持续同步 Transform。

在 `Despawning` 时注销对应 Entity 并释放其 DI 容器。房间退出时调用 `adapter.Dispose()`、等待 `adapter.Disposal`，再释放房间容器。

## 示例与进一步阅读

使用 .NET 10 SDK，在同一工作目录克隆主库和 LiteNetLib 扩展，构建完整解决方案：

```powershell
git clone https://github.com/CortexCore/Net.BITKit.Multiplayer.git
git clone https://github.com/CortexCore/Net.BITKit.Multiplayer.LiteNetLib.git

dotnet build Net.BITKit.Multiplayer/Net.BITKit.Multiplayer.slnx -c Release
dotnet run --project Net.BITKit.Multiplayer/Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build
dotnet run --project Net.BITKit.Multiplayer/Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build -- --relay
```

- [NetRpc 使用与构建配置](Docs/design-implementation.md)
- [传输层](Docs/transport-guide.md) / [LiteNetLib 接入](Docs/litenetlib-guide.md)
- [Godot 可运行样例](Samples/NetRpcGodot/HUMAN-START-HERE.md)
- [文档目录](Docs/README.md) / [当前实现与平台验证范围](Docs/current-status.md)
