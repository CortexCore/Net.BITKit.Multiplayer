# Design v2 Agent Handoff

> 历史交接，仅用于旧进度参考。用户随后明确要求完成手写 design-v1 与 design-v2 全部内容，取消本文“只做 v2 / 不做 v3”等限制。当前接线和证据见 [新链路](design-implementation.md) 与 [验收](design-implementation-validation.md)。

## 任务范围

本交接只负责完成 `Docs/design-v2.md`。

当前阶段要实现：

```text
Entity / INetworkIdentity
    -> INetComponent 注册
    -> Component fingerprint / revision
    -> Host 权威状态同步
    -> 不可靠 Component snapshot / delta
    -> Remote Interface SyncVar
    -> NetworkList / NetworkDictionary
```

当前阶段**不要实现 design-v1 中的 v3 普通类 `[Rpc]` IL Wrapper**。不要重写 `NetworkWeaveRuntime`，不要处理 Unity ILPP，不要做普通类方法的自动 `_Internal` 拆分。v3 留给白天单独实施。

## 当前仓库和已完成基线

工作目录：

```text
D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer
```

当前已经存在的 v1 原型：

- `Src/Runtime/NetRpcV1.cs`
  - `NetRpcModel`
  - `RpcContextService`
  - `NetMessageBag`
  - `RpcMap` 请求/响应
  - DI 目标解析
  - 反射 Dispatch
- `Src/Transport/TcpTransport.cs`
  - 纯 .NET `TcpListener` / `TcpClient`
  - 长度前缀 TCP 帧
  - 最小 `BITKit.Multiplayer.NetRpc.ITransport`
- `Src/Runtime/RemoteInterfaceSourceGenerator.cs`
  - 只生成 Remote Interface 源码形状
  - 尚未动态编译、尚未接入 DI 的 `AddRemoteInterface<T>()`
- `Tests/NetRpcV1Tests.cs`
  - RpcMap + DI + 返回值
  - void RPC
  - 真实 TCP loopback
  - 生成代理源码形状

当前 v1 定向测试为 4/4 通过。Core、Transport、TouchSocket 和 CodeGen 的现有测试项目可正常构建；TouchSocket 只保留为旧适配/特殊服务参考，不是本 v2 的游戏 RPC 基础设施。

## design-v2 的目标语义

### Entity 与 Component

Entity 默认由 `IEntitiesService` 注册和注销。需要同步的组件由 Entity scope 注入：

```csharp
entity.ServiceCollection.AddSingleton<NetFooComponent>();
entity.ServiceCollection.AddSingleton<INetComponent>(
    provider => provider.GetRequiredService<NetFooComponent>());
```

同步器遍历：

```csharp
entity.ServiceProvider.GetServices<INetComponent>()
```

每个 `INetComponent` 至少需要提供：

```text
ComponentId
Schema/Fingerprint
Revision/Version
当前值的 snapshot 编解码
```

只有 Entity 同时具有 `INetworkIdentity` 时才进入网络同步。v2 暂时不负责 Entity 的创建和销毁，只同步双方已经存在的 Entity。

### Host 权威修改

Client 修改组件必须走 RPC 请求 Host：

```text
Client component request
    -> 自研 RpcContext / NetRpcRuntime
    -> Host 校验 sender、权限和业务条件
    -> Host 修改组件
    -> Host 发布 Component snapshot / delta
```

Client 不得直接写入权威同步值。同步应用和 RPC 返回是两个完成条件，不能假设 `await` RPC 后 Client 已经收到状态快照。

### Component 同步

Host 遍历已注册的 `INetComponent`：

```text
读取当前 fingerprint/revision
    -> 没有变化：跳过
    -> 有变化：写入 EntityId、ComponentId、revision、fingerprint、value
    -> 通过不可靠 Transport 发送
```

接收端：

```text
读取 EntityId / ComponentId / revision / fingerprint
    -> 查找双方已有 Entity
    -> 查找 INetComponent
    -> 校验 fingerprint
    -> 丢弃旧 revision
    -> 应用 snapshot/delta
```

v2 默认使用不可靠通道。必须保留 revision、fingerprint 和边界检查；不可靠不等于可以接受旧值、未知 Entity 或错误 schema。

### Remote Interface SyncVar

Remote Interface 的方法和字段分开处理：

```csharp
public interface IFoo
{
    Task<int> PlusAsync(int a, int b);  // RPC
    int GetValue { get; }               // SyncVar
    IList<int> Items { get; }           // NetworkList
    IDictionary<int, int> Counts { get; } // NetworkDictionary
}
```

接口声明的同步字段属于网络状态。实现类型中的非接口字段不自动同步。

生成代理或实现对象通过 Context 创建网络容器：

```csharp
Items = rpcContext.GetList<int>();
Counts = rpcContext.GetDictionary<int, int>();
```

`NetworkList<T>` 和 `NetworkDictionary<TKey,TValue>` 的 Add/Remove/Set/Clear 必须产生带 revision 的操作事件。事件操作至少携带：

```text
targetId
propertyId
operation
key/value 或 index/value
revision
```

### 重要的 v2 边界

- 普通 Entity 不自动扫描和同步任意字段。
- 普通 `List<T>` / `Dictionary<TKey,TValue>` 不做深层变更观察。
- Entity 不在本阶段自动 Spawn/Despawn。
- 不可靠 Component Sync 不提供 RPC 结果等待或自动可靠重试。
- 同步值必须有明确上限、fingerprint 和 revision。
- 快照/增量断档时请求当前快照，不允许无限缓存日志。
- Hook/Changed 在状态提交后、Runtime 内部锁外触发。
- Client 直接写 Host 权威组件必须明确失败。
- 业务服务不允许手动绑定 Session、Entity、Component 或 Transport。
- 不得改 Project B Unity、Server 或 TouchSocket 旧业务代码。

## 旧 Sync worktree 参考

参考目录：

```text
D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.Sync
```

优先阅读：

```text
Src/Runtime/SyncCollection.cs
Src/Runtime/SyncList.cs
Src/Runtime/SyncDictionary.cs
Src/Runtime/SyncHashSet.cs
Src/Runtime/SyncChanges.cs
Src/Runtime/SyncWire.cs
Src/Runtime/RpcRuntime.Sync.cs
Tests/SyncCollectionTests.cs
Tests/SyncAllocationTests.cs
Tests/Fixtures/SyncState.cs
Docs/sync-collections-guide.md
Docs/sync-collections-validation.md
```

旧 worktree 的行为可以作为状态操作、Hook、fingerprint、snapshot、delta、revision recovery 和 GC 的参考。移植时使用当前 `Net.BITKit.Multiplayer` 的新 v1 Transport/DI 边界，不把旧的业务绑定 API 扩散回来。

## 推荐实施顺序

### P0：契约

1. 定义纯 .NET `INetworkIdentity` / `INetComponent`。
2. 定义 ComponentId、fingerprint、revision 和 snapshot/delta 数据契约。
3. 定义 Host 权威组件写入与 Client 请求的最小 RPC 接口。
4. 明确可靠请求与不可靠状态帧的 channel/delivery 边界。

### P1：组件注册和状态收集

1. 在 `IEntitiesService` 生命周期建立 Entity → `INetComponent` 查询。
2. 建立 Entity/Component 状态缓存。
3. 实现 fingerprint/revision 对比。
4. 实现不可靠 Component snapshot 发送与接收。
5. 实现旧 revision 丢弃、未知 Entity 丢弃和 fingerprint 错误报告。

### P2：集合和字段同步

1. 移植 `SyncList<T>` 的核心操作和变更事件。
2. 移植 `SyncDictionary<TKey,TValue>` 的核心操作和变更事件。
3. 实现 `NetworkList` / `NetworkDictionary` 的 snapshot 和 operation delta。
4. 支持接口 SyncVar 标量属性。
5. 处理晚加入、断档恢复和重复版本。

### P3：v2 Sample

创建纯 .NET 可运行 Sample，建议包含：

```text
Host Entity: player-1
  HealthComponent
  InventoryComponent
  Inventory.Items: NetworkList<int>
  Inventory.Counts: NetworkDictionary<int,int>

Client Entity: 同一个 player-1
  接收 Health/Inventory/集合状态
  通过 RPC 请求 Host 修改 Health/Inventory
```

Sample 游戏层只能有普通 C# 类、`INetComponent` 和 RPC 标记/契约。禁止出现 `BindService`、`BindEntity`、`BindComponent`、TouchSocket、裸 socket、手写 packet 或 Transport 选择逻辑。

## 验收标准

v2 完成前必须通过：

1. `dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo`。
2. Core v2 定向测试：Entity/Component 注册、fingerprint、revision、snapshot、旧值丢弃。
3. `NetworkList`：Add/Remove/Set/Clear、重复操作抑制、snapshot/delta、断档恢复。
4. `NetworkDictionary`：Add/Set/Remove/Clear、key/value 校验、snapshot/delta、断档恢复。
5. Client RPC 修改 Host 组件，Host 权威值随后同步回 Client。
6. Client 直接写权威组件被拒绝。
7. 两个独立 Room 中相同 EntityId/ComponentId 不互串。
8. 真实 TCPTransport loopback Sample 可运行；不依赖 TouchSocket。
9. Sample 的游戏层不出现任何 `Bind*` 网络接线。
10. v3 `[Rpc]` 普通类 IL Wrapper 不在本轮验收范围内。

## 下一位 Agent 的第一步

先读本文件、`Docs/design-v2.md`、`Docs/current-status.md` 和旧 Sync worktree 的 `Docs/sync-collections-guide.md`，然后检查当前 v1 工作树。不要实现 v3，不要修改 Unity Project B，不要恢复 TouchSocket 作为游戏 RPC。先提交 v2 的契约和一个最小 `HealthComponent` snapshot loopback 测试，再逐步加入 List/Dictionary。
