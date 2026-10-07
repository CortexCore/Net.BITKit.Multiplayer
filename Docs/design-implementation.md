# 手写设计的实现与使用

日期：2026-10-03。需求依据：[design-v1](design-v1.md) 与 [design-v2](design-v2.md)。历史 Agent 交接是进度线索，不是范围限制。

两份设计的 .NET 实现使用 `BITKit.Multiplayer.NetRpc`：MessagePack、自研字节 Transport、生成远程接口、普通类 IL Wrapper、可靠/不可靠通道、Relay、ECS 组件和接口状态同步。旧 `RpcRuntime`/B6 类型仅保留为兼容 runtime；网络接线统一使用本页的原生 Transport。

当前主工作树的默认异步栈为 **UniTask 2.5.10**，业务契约/receiver/请求等待器/Runtime/transport 已端到端迁移；Task/ValueTask 是显式兼容边界。[API 与线程规则](netrpc-unitask-default.md) · [合入后的 .NET / Unity 证据](netrpc-main-integration.md)。

## 逐项对照

| 手写目标 | 实现入口 | 验收 |
| --- | --- | --- |
| v1 RpcContextService / NetRpcModel / MessagePack 参数 | `Src/Runtime/NetRpcV1.cs` | 参数、void、返回值、真实 TCP |
| 未知 ID 挂起、向发送端请求 RpcMap、DI 解析 | `ReceiveCall / SendMap / ReceiveMap` | 普通目标与没有 Client 实现类型的代理补表 |
| v2 原生远程接口与 DI | `RemoteInterfaceSourceGenerator.cs`、`NetRpcServices.cs`、`PrecompiledRemoteInterfaces.cs` | 编译后的代理、Task/ValueTask/UniTask、null、异常、异步完成 |
| v2 强类型句柄与低 GC | 生成的 `__netrpc_recv_*`、池化 bag/reader/invocation | 真实生成接收器；预热标量写入和同步发送 0 B |
| v3 普通类 RPC 编织 | `Src/Editor/CodeGen/NetRpcWeaver.cs`、`CodeGen --netrpc` | 实际 woven DLL、接口/具体类、原业务体、嵌套、Host/All |
| v4 复合可靠/不可靠 Transport | `Src/Transport/TcpTransport.cs` | TCP + UDP、端点 proof、端口重映射、禁止不可靠返回值 |
| v5 Direct / Relay / Host 可选重连 | `Src/Transport/NetRpcRelay.cs` | 同一 Client 连接 API、TCP/UDP 两跳、Relay 后上线/重启恢复、Direct 独立运行 |
| ECS 注册与组件变更同步 | `NetEntities.cs`、`NetRpcState.cs` | Identity 筛选、DI 查询、指纹、版本、未知实体/旧值/schema 拒绝 |
| 默认同步接口标量 | `RegisterState / RegisterRemoteState / PublishStateAsync` | 初始与后续状态、两房间隔离 |
| IList / IDictionary 网络容器 | `NetworkCollections.cs` | Add/Insert/Set/Remove/Clear、重复抑制、快照、增量、断档恢复、原子提交 |

证据与命令见 [本轮验收](design-implementation-validation.md)。

## 构建期生成：默认路径

共享接口放在独立 Contracts 程序集：

```csharp
public interface IFoo
{
    UniTask<int> Plus(int a, int b);
    int GetValue { get; }
    IList<int> Items { get; }
    IDictionary<int, int> Counts { get; }
}
```

在使用它的应用项目引用 Core/Transport、CodeGen 构建依赖，并导入 `Tools/NetRpc/NetRpc.targets`：

```xml
<PropertyGroup>
  <NetRpcContractsAssembly>共享 Contracts DLL 路径</NetRpcContractsAssembly>
  <NetRpcWeave>true</NetRpcWeave>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="CodeGen/BITKit.Multiplayer.CodeGen.csproj"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
<Import Project="Tools/NetRpc/NetRpc.targets" />
```

路径相对当前项目调整；完整项目参考 `Samples/NetRpc/NetRpc.Sample.csproj`。构建顺序：

1. `CodeGen --remote contracts.dll generated.cs`：生成原生接口代理和强类型接收器。
2. 编译生成源码与业务代码。
3. `CodeGen --netrpc input.dll output.dll`：生成普通类发送 Wrapper 与直接原业务体接收器。

编织输入是原始 MSBuild intermediate DLL，输出是应用 DLL。每次 Build 都从原始 DLL 生成编织输出，避免增量 copy 将输出还原为未编织程序集；不会对已经编织的输出再编织。

默认 `PrecompiledRemoteInterfaceFactory` 使用已编译类型，Core 不依赖 Roslyn，也不在每次调用时生成代码。

可选 JIT 路径：引用 `Projects/BITKit.Multiplayer.RemoteCompiler.csproj` 并调用 `services.AddGeneratedRemoteInterfaces()`。它将接口源码编译一次，随后调用原生代理/委托；Roslyn 源码在仓库 `RemoteCompiler/`，不进入 Unity UPM 的 `Src/` 自动编译范围。AOT 应使用构建期路径。

## DI 与调用

每个房间使用独立的 ServiceProvider / RpcContextService 和 scope，接线在 composition root：

```csharp
hostServices.AddNetRpcService<IFoo, Foo>();
hostServices.AddNetRpc(true, _ => hostTransport, scope: 42);

clientServices.AddRemoteInterface<IFoo>();
clientServices.AddNetRpc(false, _ => clientTransport, scope: 42);

// 业务仅注入 IFoo。
var result = await foo.Plus(20, 22);
var localSynchronizedValue = foo.GetValue;
```

接口与实现的 DI 别名是同一实例。普通类用 `AddNetRpcObject<MyActor>()`，解析时自动进入当前 Runtime；业务不调用 BindService/BindEntity/BindComponent。

UniTask 等待远端完成，UniTask<T> 等待结果；默认单次消费，Core 引用纯 .NET UniTask 2.5.10，仍构建 netstandard2.1 / 无 UnityEngine 直接引用。Task/ValueTask authored RPC 仍支持，显式 `RequestTask` / `RequestValue` 是互操作表面，默认 UniTask 生成/编织链路不经 AsTask。void 单向，无成功 ACK/等待器；取消/超时结束等待，不撤销业务也不提前归还仍在发送的 buffer。共享超时扫描约 10ms；旧连接回复、迟到发送失败按世代与 request ID 隔离。跨 UniTask await 的业务身份在入口保存不可变 Current；Unity 对象访问由主线程 adapter/显式 SwitchToMainThread 负责。

远程接口属性 getter-only，读取本地状态；初始快照到达前是默认值。只同步接口声明成员。声明集合用 IList<T>/IDictionary<TKey,TValue>，Host 实现用 NetworkList<T>/NetworkDictionary<TKey,TValue>。

## 普通类 RPC

```csharp
public sealed class Actor
{
    [Rpc(SendTo.Host)]
    public UniTask<int> Fire(int damage) => UniTask.FromResult(damage);

    [Rpc(SendTo.All, RpcDelivery.Unreliable)]
    public void Pose(int entityId, MyPosition position) { /* 本地表现 */ }
}
```

接口和具体实例调用都经过 Wrapper。接收器直接调用移出的原业务体，不回送同一 RPC；不同嵌套 RPC 正常路由。通过接口注册 woven 实现时，保留接口 ID、具体方法 ID、额外普通 RPC 和直接原业务体接收器。

Host 的 Host-directed 本地调用直接把原参数交给原业务体，不序列化往返；All 在本地执行前冻结出站参数，然后本地业务体直接运行一次并发送给 Clients。显式权限检查和上下文恢复覆盖这两个路径。

新 backend 覆盖手写 Host/All 路线。All 只允许 Host 发起，Host 自身执行一次；All 与 Unreliable 必须 void。不支持的同步结果、静态/开放泛型/ref/out/async void 等由真实编织阶段诊断。旧 backend 的 Target 等扩展另看旧指南。

## Transport 与 Relay

NetRpc.ITransport 只处理字节：OnReceived、Send、SendFast。业务类型、方法名、实体解析不进入 Transport。TcpTransport 是同一 admission 的复合连接：

- TCP 长度前缀可靠帧，串行发送，复用发送头和接收缓冲。
- UDP 交换随机连接凭据、自动 proof/ACK，学习实际映射端口；数据与 proof 独立标记，支持任意业务首字节。
- Send 借用内存到实际完成；OnReceived 仅借用到同步 callback 返回。
- 接收器先同步解码，再调用异步业务。延后的 RpcMap 调用和请求回复拥有自己的数据。

Client 对 Direct Host 或 Relay 都调用 `TcpTransport.ConnectAsync(endpoint.Host, endpoint.Port)`。Host 保持 Direct listener，并用 RelayHostConnection 显式启动独立 sidecar；失败每 300ms 重试，不改变 Host 角色或阻止 Direct。每个 RelayEndpoint 对应一个房间/权威 Host，多房间使用独立 endpoint/scope。

Relay 逻辑 peer 绑定到实际连接，Host 注册校验 relay host key。连接身份不是调用者包头声明的 sender。PeerId 1 保留给 Host，Direct Client 默认 2，Relay 分配其他 ID。NetRpcCallContext.Current 在 invocation 入口提供真实 sender/target，入口与嵌套 scope 显式恢复；UniTask 不自动流动 ExecutionContext，跨 await 使用身份时保存 Current 到局部变量。

UDP 凭据和 relay key 是传输接线凭据；业务账号、权限与所有权通过应用策略，例如 `runtime.Authorize = (sender, target, method) => CheckPermission(...)`。

## Entity / Component

```csharp
entityServices.AddSingleton<HealthComponent>();
entityServices.AddSingleton<INetComponent>(p => p.GetRequiredService<HealthComponent>());
entityServices.AddSingleton<INetworkIdentity>(new NetworkIdentity(entityId));
entitiesService.Register(new NetEntity(entityProvider));
```

Runtime 通过 IEntitiesService 注册/注销缓存 Entity 的 GetServices<INetComponent>()；无 Identity 不同步。Entity provider 的所有权属于创建它的 Entity 生命周期。

NetComponent<T> 提供 Host 写入检查、值/schema 指纹、revision、MessagePack 快照、Changed；自定义 INetComponent 要原子地 CaptureSnapshot 值与 revision。

先比较组件指纹，没变化跳过；有变化发送 EntityId、ComponentId、schema、revision、值校验指纹、快照。接收端只查找本地已有 Entity/Component，拒绝未知 ID、schema/值指纹错误、旧 revision。ECS 使用不可靠完整快照。远端对象生成/销毁由可选的 Core `NetworkObjectService` 负责：引擎 adapter 创建并绑定后，复用本 `IEntitiesService` 注册，通过可靠完整组件快照建立初始 Ready 屏障，之后继续原组件同步；见[引擎无关网络对象](network-objects.md)。

Host 默认 100ms 收集变化、2s 全量状态，修复丢失最后一次 UDP 更新。NetRpcOptions 可调整；自建 Runtime 可 StartSynchronization 或 PublishStateAsync。这是状态修复，不重试有副作用 RPC。Client 修改通过 Host 业务 RPC，RPC 完成和快照到达是不同条件。

## 标量和集合

- 标量变化带 schema/revision，getter 读 Client 缓存。
- Host 集合提交后发送 targetId/propertyId/schema/revision/op/index-or-key/value。
- 初始/迟加入请求当前快照；重复/旧版本丢弃，断档请求快照，无无限操作日志。
- 非法索引、重复快照 key、截断/多余参数不部分提交集合。
- Changed 在提交后、集合/状态锁外触发。
- Client 组件 setter 和集合修改明确失败。
- 可变 DTO 在 Client 读取和容器出入边界复制；修改读取的 DTO 不绕过权威，也不代替显式 Set。
- Dictionary key 为独立标量/值类型，每容器上限 4096 项。

普通 List/Dictionary 不注入变更观察；同步接口必须明确声明网络容器契约。Component 可以对完整值序列化并算指纹。

## 线格式与上限

25 B 头：command byte、target uint、method uint、request uint、count int、scope ulong，然后逐项长度前缀 MessagePack。command 首字节与 Relay 控制前缀不冲突。方法 ID 包含参数/返回类型；字符串只在 RpcMap 冷路径。

序列化 payload 上限 1 MiB，TCP 给 RPC 头和 Relay 6 B 封装预留空间。SendFast payload 上限 60000 B，UDP 另含 17 B 凭据/数据标记；Relay 需要额外留出 6 B。无分片、可靠回退或不可靠 RPC 返回。

pending RPC 上限 4096，补表挂起上限 256，Transport 初始 backlog 上限 32。scope 要匹配；无全局 active runtime / 全局 target 路由。

## 环境证据

Windows .NET 10/8 的实际生成、编织、loopback 网络与 NuGet UniTask 2.5.10 通过；Core 构建 netstandard2.1。Project B 曾在 Unity 2022.3.41f1 完成 MessagePack/Annotations 3.1.8、StringTools 17.11.4 的显式引用与实际编译/DLL 加载。后续 **Unity 2022.3.62f3 Edit Mode 新 backend 的原生代理、ILPP、公开 Session、真实 TCP+UDP、Task/UniTask/状态/主线程和窗口/Ready reload 退出均通过**，见 [Unity 接入与验收](unity-netrpc-validation.md)。跨 Unity 进程、完整游戏、Player/IL2CPP、Linux、公网和真实 NAT 路由器仍未验收。NAT **端口重映射**用真实 UDP socket 模拟验证；Edit Mode 或旧 backend 的证据不代替这些后续关卡。
