# SyncVar 集合首版验收 — 2026-09-28

后续状态：本文保留 sandbox 首版检查点；GC 优化及本功能现已合入主库 master 工作树，并完成 [Unity Edit Mode 接入](unity-sync-collections.md)。

## 工作区与交付范围

- worktree：`D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.Sync`
- 分支：`feature/sync-collections`（尚无初始 commit）
- 基线 tree：`e80cbfa21dbff8cb122babe27a9639f14f91557e`，引用 `refs/feature-baselines/sync-collections`。
- 基线来自已完成的 GC sandbox，包含其优化。GC 目录、live 库与 Unity 工程未在本轮修改；没有 commit、push、合入或 Unity manifest 变更。
- 交付：SyncDictionary/SyncList/SyncHashSet、Host 权威、类型化 Changed 与编织 Hook、状态契约指纹、完整快照/增量/版本恢复、原子混合批次、生命周期与上限，以及可独立执行的 .NET 示例。

API、协议与 DTO 所有权以 [使用指南](sync-collections-guide.md) 为准。

## 实际执行

| 检查 | 结果 |
| --- | --- |
| Core/编织/真实 socket 测试工程 | **90/90** |
| 其中新增 SyncCollectionTests | **19/19**，包括 Direct 与 Relay 两个真实 socket case |
| Arena.GameTests 回归 | **30/30**，覆盖现有权威、Health SyncVar 与 Relay 场景 |
| 独立 SyncCollections 控制台示例 | **通过**，实际构建期编织和 TCP/DMTP |
| Core/native/TouchSocket netstandard2.1 构建 | **0 警告、0 错误** |

Core 90 项包含原有 TypedHot 的同步零分配断言。没有重跑完整 solution 全部 suite、Arena 多进程 profiling 或图形验证；不能把本次 120 项定向回归写成全量测试结果。

```powershell
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --nologo
dotnet test Samples/Arena/GameTests/Arena.GameTests.csproj -c Release --nologo
dotnet run --project Samples/SyncCollections/SyncCollections.csproj -c Release
dotnet build Projects/BITKit.Multiplayer.TouchSocket.csproj -c Release -f netstandard2.1 --nologo
```

示例实际输出包括：

```text
Client Items: Reset, v0, Snapshot
Initial snapshot: items[1]=5, tasks=2, unlocked=2
Host Items: Set, v1, Local
Client Items: Set, v1, Remote
Host RevisionCount: 0 -> 1
Client RevisionCount: 0 -> 1
Host accepted client: +2 at slot 1
RPC result=7; replicated items[1]=7; sender verified by Host
Client direct mutation correctly rejected.
Sync collections demo passed.
```

两端输出相对顺序受真实异步调度影响，不作为跨节点事件全序承诺。

## 关键覆盖

- **真实编织**：fixture 编译后通过共享 Cecil weaver，再加载编织程序集；接口/继承与现有 RPC 回归继续通过。错误 Hook（按值 collection 参数、async void、无 SyncVar）、可替换集合属性、不稳定 Key 类型被构建期诊断。
- **权威与身份**：Client 三种容器直接修改均被拒绝；真实 Direct/Relay Client RPC 在 Host 执行，Host 收到真实 sender，Client 业务体不执行；随后集合和标量状态抵达 Client。
- **变更语义**：增删改清空、无效操作抑制、Host Local/Client Remote/快照 Reset、标量 old/new、异常 Hook 不回滚且后续观察者继续。
- **原子批次**：混合增删改是一个版本、一个增量包；Host 的非法后续操作和 Client 的恶意第二 opcode 都不能留下半次写入。所有批次通知读取完整已提交内容。
- **真正增量**：单项修改包大小不随 Dictionary 从 1 项增长至 256 项而增长；完整快照明显更大，不以每次重发整份集合冒充增量。
- **DTO 所有权**：写入对象、读回对象、TryGetValue、Values/枚举值和事件 DTO 的修改均不能改变内部集合；修改副本再赋回可以同步；等内容 DTO 替换不增加版本。
- **指纹**：错误 wire 指纹拒绝且不推进版本；构造缺少一个声明成员、其余成员完全相同的实际程序集，Bind 仍能检测整体状态契约错配，集合未应用，失败绑定已解除。
- **恢复**：丢失增量后通过快照恢复；丢失一次恢复响应后自动重试成功；重放不重复 Hook；显式 RequestStateSnapshot 可用于幂等恢复。
- **生命周期**：迟绑定、Unbind 后状态恢复、RemoveTarget 墓碑、Host 离场，以及绑定恢复期间写入拒绝。对象/集合实例保持稳定。
- **并发与积压**：8 个 Host writer 的 80 次提交形成同一版本流；128 个在途/排队增量上限在修改前拒绝；解绑丢弃排队内容，活动底层发送保持其所有权至完成。
- **容量**：256 元素、64 操作/批次、16 KiB 编码值和超长元素拒绝，失败不改变现有内容。

原有两项人工构造 state payload 的测试最初因仍注入旧格式而失败；已让测试辅助函数构造新指纹头，保留超长/截断等负面输入，完整 Core 回归随后通过。

## 性能与平台边界

后续已补做 [专门的 GC 定位与优化](sync-collections-gc.md)：Core 93/93、Arena.GameTests 30/30；真实 TCP 单项 int 更新约 8208→944 B。上面的 90 项表保留为功能首版检查点。没有以功能通过代替分配测量。

- 集合编解码和 Changed 使用泛型类型；Hook 是生成 shim + 冷创建 delegate，集合通知无按次 object[]/MethodInfo.Invoke。
- 快照按需编码，普通编辑发送增量；显式批次摊薄信封和调度开销。
- **未声明集合零 GC**：独立拥有的可靠帧、staging store、DTO 防御性复制、字符串、回调记录与真正异步操作仍分配。没有本功能的新 Arena 全进程 KB/s 结论。
- 标量 SyncVar 原有反射 setter/值编码路径没有在本轮改造成 typed 零分配路径。
- Unity/Mono/IL2CPP/裁剪、Unity 主线程绑定、Linux、公网和长期高人数负载未在此 worktree 验证。新增 .meta 只是包源文件身份，不是 Unity 导入成功证据。

## 交接

1. `git diff refs/feature-baselines/sync-collections` 审查本功能，排除 `AGENTS.md` / `SYNC-WORKTREE.md` 管理内容。
2. 若需要连同 GC 优化一起审查，比较 `refs/perf-baselines/gc-isolated`；当前 worktree 已包含 GC 基线，不要重复覆盖 live 文件。
3. 合入前逐文件检查其他 Agent 的并行修改，尤其共享 Weaver/Runtime；不能直接复制目录。
4. 状态 payload 已升级。Host/Client 更新 Core 后必须重新编织业务程序集，再让业务 Agent 按新指南接入；不能混跑旧 SyncVar 格式。
5. 本轮完成的是可独立运行的 .NET 功能版本；下一关是批准合入后的 Unity SyncVar/集合与主线程生命周期验证，以及 Player/AOT。
