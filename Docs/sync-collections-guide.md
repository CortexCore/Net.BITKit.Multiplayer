# SyncVar 集合与 Hook（首版）

实现已由 `Net.BITKit.Multiplayer.Sync` 合入主库 master 工作树，继承 GC 优化并通过 Unity Edit Mode 真实会话验证。Core 是 C#9/netstandard2.1；编织使用共享 Cecil 实现。[.NET 验收](sync-collections-validation.md) · [主库/Unity 接入](unity-sync-collections.md)。

## 最小用法

```csharp
public sealed class InventoryState
{
    [SyncVar, Hook(nameof(OnHealthChanged))]
    public int Health { get; private set; } = 100;

    [SyncVar, Hook(nameof(OnItemsChanged))]
    public SyncDictionary<int, int> Items { get; } = new SyncDictionary<int, int>();

    [SyncVar] public SyncList<string> Tasks { get; } = new SyncList<string>();
    [SyncVar] public SyncHashSet<int> Unlocked { get; } = new SyncHashSet<int>();

    private void OnHealthChanged(int oldValue, int newValue) { }
    private void OnItemsChanged(in SyncDictionaryChange<int, int> change) { }

    [Rpc(SendTo.Host)]
    public Task<int> AddItems(int slot, int amount)
    {
        // Bind 的 owner/authorize 根据真实 RPC sender 授权；这里继续验证业务条件。
        if (slot < 0 || slot >= 64 || amount < 1 || amount > 10)
            throw new ArgumentOutOfRangeException();
        Items.TryGetValue(slot, out var count);
        Items[slot] = checked(count + amount);
        return Task.FromResult(Items[slot]);
    }
}
```

普通业务对象通过现有 `runtime.Bind(key, instance, authorize, owner)` 接入。业务显式 new 容器；集合必须是 **getter-only 自动属性**，不能在绑定后替换实例。Weaver 验证声明和 Hook，Runtime 冷绑定时接线；不需要手动注册每个容器。

Host 与 Client 各拥有自己的对象及 Runtime，不是双角色 Host。绑定前可填充初值，不发包、不触发 Hook/Changed；Host 首次绑定将内容作为版本 0。Client 接收快照时替换内部内容、保留容器实例。

## 权威与业务 RPC

- 绑定后的所有公开修改入口（包括 Clear、空批次和其他无效修改尝试）要求 Host；Client 直接调用会收到 `RpcException`，正常连通时错误为 `InvalidRole`。
- Client 通过 RPC 发送意图。Host 验证真实 sender、owner/authorize 和业务规则，再修改集合。
- 状态仅接受当前房间的 Host 连接来源；RPC 结果与状态应用是两种完成条件，不能假设 `await request` 后 Client 状态已经抵达。
- 绑定初始化期间、解绑后及目标移除后拒绝修改。Unbind 保留逻辑状态，重新绑定同一 key 可恢复；RemoveTarget 保留原有墓碑语义。房间 scope、成员准备状态及状态可见性授权仍生效。

## 操作与原子批次

| 容器 | 普通操作 | 批量便捷操作 |
| --- | --- | --- |
| SyncDictionary<TKey,TValue> | Add、索引赋值/新增、Remove、Clear | SetRange |
| SyncList<T> | Add、Insert、索引赋值、Remove（首个匹配）、RemoveAt、Clear | AddRange |
| SyncHashSet<T> | Add、Remove、Clear | UnionWith |

三者还提供混合操作的 `ApplyBatch(params Edit[])`：

```csharp
Items.ApplyBatch(
    SyncDictionary<int, int>.Edit.Remove(fromSlot),
    SyncDictionary<int, int>.Edit.Set(toSlot, count));
```

- 一次成功批次生成 **一个版本、一个增量 payload**，不是每项一个包。
- 全部验证/编码成功后提交；非法后续操作不会留下前面的半次修改。Client 对完整批次先校验到 staging store，再提交。
- 每个实际操作保留一条类型化通知，顺序与批次一致；回调读取集合时看到的是**整批提交后的内容**。
- 无效删除、相同值替换、HashSet 重复添加被抑制；全批无变化不增加版本。
- 单次调用默认立即进入发送队列；没有隐含 Unity tick，也没有自动将一个 tick 中的全部调用合并。需要原子合并时显式使用批次 API。不同集合之间没有跨成员事务。

## Changed 与 Hook

Hook 只支持本声明类型上的同步、非泛型实例 void 方法：

```csharp
void ScalarChanged(int oldValue, int newValue);
void ListChanged(in SyncListChange<string> change);
void MapChanged(in SyncDictionaryChange<int, int> change);
void SetChanged(in SyncHashSetChange<int> change);
```

编织器生成直接调用 shim；冷绑定建立类型化 delegate。集合通知不经过 `object[]` 或按次反射 Invoke。也可手动订阅容器 `Changed`；每个手动订阅者和声明式 Hook 都是独立观察者，不要重复注册同一个处理方法。

| Operation | 有效信息 |
| --- | --- |
| Dictionary Add / Set / Remove | Key；Add 的 NewValue，Set 的 OldValue/NewValue，Remove 的 OldValue |
| List Add / Insert / Set / Remove | Index；对应的 NewValue / OldValue |
| HashSet Add / Remove | Value |
| Clear / Reset | 操作和版本；不复制整份旧集合，Key/Index/旧新值不应读取 |

变更带 `Version` 和 `Origin`（Local、Remote、Snapshot）。首次或恢复快照发一次 Reset，不重放 N 次 Add。重复/旧版数据不重复通知。Host 本地提交与 Client 远端应用分别通知一次，回调异常通过 `UnhandledDispatch` 报告，不回滚状态，也不阻止其他观察者。

通知在 Runtime/集合锁外执行。同一集合的回调重入写入被明确拒绝；可在回调返回后安排后续业务操作。集合通知按提交顺序排队；回调不应阻塞等待网络往返。

Core 不承诺 UI 主线程。Unity 宿主须继续通过主线程 wire/调度器应用网络消息，Host 业务写入也应在约定线程执行。容器单次访问/修改受锁保护；跨多次调用和枚举仍由业务执行序列负责。

单操作接收先完整校验后原地提交；多操作/快照使用可复用的内部 staging store。不要把枚举器或遍历视图当成跨后续修改仍稳定的快照。分配对照见 [集合 GC 记录](sync-collections-gc.md)：真实 256 项 int Dictionary 单项更新从约 8208 降至 944 B/投递（Host+一个 Client），仍不是零 GC。

### 初始化完成

标量相同值不触发变化 Hook，包括首次快照值与本地默认值相同时。需要初始化 UI 时，在 Bind 前订阅 `RpcRuntime.Synchronized`：

```csharp
runtime.Synchronized += key => RefreshWholeView(key);
```

事件在本次绑定所有已声明状态成员都有初值后触发一次；它是初始化屏障，不代表这些成员来自同一模拟 tick。后续使用 Hook/Changed。事件观察者异常同样隔离。

## 元素与所有权

- 值类型、string 及现有受支持的显式 MemoryPack DTO schema 可作 List 元素和 Dictionary 值；DTO 内不能含嵌套集合、循环、多态或自定义 formatter/callback。
- Dictionary Key、HashSet 元素首版只允许 primitive、enum、string，使用默认相等性；拒绝 null key/元素和任意自定义 comparer。HashSet 不保证跨端遍历顺序，网络不传 `GetHashCode()`。
- 可变 DTO/含引用值类型在写入时深复制，在读取、枚举与事件交付时提供副本，阻止调用方或 Hook 经别名改变内部权威内容。固定无引用值类型与 string 不走这种复制。
- 修改 DTO 应读取、修改副本、赋回：

```csharp
var item = ItemsWithDto[slot];
item.Count += amount;
ItemsWithDto[slot] = item;
```

直接 `ItemsWithDto[slot].Count++` 只修改读取副本，不会同步。DTO 按支持的序列化内容比较；复制/字符串/DTO 解码有分配，首版不承诺集合零 GC。标量 SyncVar 仍是原有整体赋值机制，不具备深层变更追踪。

## 指纹与协议

- Weaver 为每个成员生成 `WovenSyncVarAttribute`，包含声明身份、成员名、集合种类及完整元素/key/value schema；复用 enum 值/underlying、struct layout、MemoryPack order 等指纹规则。
- Runtime 冷绑定时将对象全部状态成员的有序指纹合入每成员 wire 指纹，因此缺少/新增成员也会不匹配。内容修改不会改变指纹；无按包反射扫描，也没有动态 Host 协议表。
- 外层仍是 B5/v3 可靠信封。`state` 携带完整值/快照，新增 `stateDelta` kind 9 携带集合增量。
- Value 内新增 19 B 头：magic `0x53`、state format 1、kind（0 标量/1 List/2 Dictionary/3 HashSet）、64-bit 指纹、64-bit base revision，均用明确小端整数。快照 base 为 -1；增量从 base 到外层 Version=base+1。
- 指纹在解码/应用前检查；错配明确报告 InvalidPayload，不应用内容。**所有状态参与端必须升级 Core 并重新编织业务程序集**，旧 SyncVar payload 与本版不互通。

## 恢复、上限与资源

首次/晚加入/迟绑定发送快照；快照按需生成，不在每次编辑后重编码整份集合。增量断档时不缓冲无界日志，而是请求当前快照。请求最多 128 个待恢复成员，500 ms 节流；单轮初次请求加两次重试，仍失败则报告 Timeout。后续断档可重新启动恢复；业务也可调用 ready Client 的 `RequestStateSnapshot(key)`。重复的同版本快照能结束恢复而不重复 Reset。

| 限制 | 首版值 |
| --- | ---: |
| 每集合元素 | 256 |
| 快照/增量 Value（含头） | 16 KiB |
| 每批输入操作 | 64 |
| 每集合待通知记录 | 256 |
| 每 Runtime 集合发送积压（含活动发送） | 128 |
| Runtime 已绑定/缓存状态成员总预算 | 4096 |

可靠集合发送有单独的有界顺序 writer；积压已满时在修改前拒绝。这个 writer 不承诺慢 Peer 隔离。解绑会清除尚未提交 I/O 的对应队列项；已经交给底层的 frame 等真实 Send 完成才释放所有权。底层永不完成的非合作任务仍会占用其有界槽，不能强行释放正在借用的内存。

## .NET 验证

```powershell
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --filter FullyQualifiedName~SyncCollectionTests
```

测试项目从实际编织的 fixture 验证三种集合的初始快照、RPC 修改、集合和标量 Hook、版本恢复、所有权以及 Client 直接写拒绝。已退役的独立 socket 示例不再是当前运行入口；新业务应使用 `Samples/NetRpc` 的原生 Transport 和状态 API。
