# 主工作树合入与 Unity 集合接入 — 2026-09-28

## 合入结果

GC、SyncVar 集合/Hook 和集合 GC 优化已合入 **`Net.BITKit.Multiplayer` 的 master 工作树**。Unity 继续使用既有 `file:../../Net.BITKit.Multiplayer/Src` 本地包，没有切换到 sandbox。

本仓库仍无初始提交：此次是按固定 tree 基线审查并应用源码差异，**不是 Git merge commit**。未创建 commit/push。合入前主库与 `refs/perf-baselines/gc-isolated` 一致，库侧无冲突；主库原始 index 未改。`refs/integration-baselines/pre-sync` 保留合入前源码树。同步分支的 AGENTS 与工作树管理文件未转移。

合入后在主库执行 `SyncCollectionTests`：**22/22 通过**。此前完整 Core 93/93、GameTests 30/30 与分配对照仍是 sandbox 检查点；本轮不冒充重新跑完整 suite。

## Unity 实际读回

- 项目：`Com.Project.B.Unity`。
- Editor：**2022.3.41f1**，Edit Mode，全程未进入 Play。
- Package resolvedPath：`D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer\Src`。
- 实际 Core：`Library/ScriptAssemblies/Net.BITKit.Multiplayer.dll`。
- 实际 probe 程序集：`Project.B.NetworkComponentRpc.Editor`。
- Unity MCP 导入/重编译后，**无编译错误**；现有项目 warnings 未当作零警告结果。
- 当前 `map_prototype.unity` 最终 `dirty=False`。

使用真实 Observation Join → 限时票据 → BITKit TCP/UDP sidecar → GameRpcSession 主线程 Pump。不是 MCP 临时 C# 中伪造的 RPC，也不是纯内存测试 wire。

菜单：**Tools > BITKit > Multiplayer > Game Session Smoke**。

新增实际编译模型 `Assets/ProjectBObservation/Editor/TypedComponentProbe/SessionSyncProbe.cs`，扩展现有 GameRpcSessionSmoke。Unity ILPP 的标记读回：

| 成员 | WovenSyncVar fingerprint |
| --- | --- |
| Health | `2c3df542e777b901` |
| Items | `9c4965c5ea8ae640` |
| Tasks | `11c5f838dcda28e6` |
| Unlocked | `992cf6461348b9a4` |

这些是编织期成员指纹；运行时另合入整个状态契约。`Change` 的 WovenTypedRpc 标记及四个生成 Hook shim 均存在。

## 行为结果

`GameRpcSessionSmoke.GetStatus()` 在测试完成时返回 `Status="Passed and stopped"`、`Passed=true`、`Error=""`。

| 检查 | 读回 |
| --- | --- |
| DI alias、Ready、真实调用者 | true |
| 原有 RPC Add/Read | Host=7，远端结果=7，Client 业务体计数=0 |
| SyncWoven | true |
| 三种集合初始快照 | Items[1]=5，Tasks.Count=2，Unlocked 含7，三次 Reset |
| RPC 驱动增量 | 结果=23，Health=23，Items[1]=23，Tasks.Count=3/首项 changed，Unlocked 含9 |
| Host/Client Hook 与业务体主线程 | SyncMainThread=true |
| 真实 sender / Client 不执行 Host 业务体 | SyncSenderCorrect=true |
| Client Hook 次数 | 标量1、Dictionary2、List3、HashSet2；SyncHookCounts=true |
| 保持原集合对象引用 | SyncReferenceStable=true |
| Client 直接写拒绝 | SyncClientWriteDenied=true |
| 单项与原子 List 批次版本 | SyncMapVersion=1，SyncListVersion=1 |
| EndWorld 后旧集合不可写 | SyncDetached=true |
| 原有组件位姿、旧 world 回调丢弃、租约释放 | ComponentPose/OldWorldDropped/BindingsReleased=true |

测试结束自动关闭连接、provider 和自有 PreviewScene。报告存于静态诊断对象，后续 domain reload 会重置为 Idle；Idle 不表示历史结果失败，重新运行菜单可获取新一轮结果。

## 一处外部编译阻塞

合入时共享 BITKit 的 Tick 调试窗口存在并行改动：静态 `DrawEvent` 引用了实例 `_autoDebugRunning`，导致 CS0120。核对五个调用均位于窗口实例后，只将 `BITKit/Src/Unity/Scripts/Tick/UnityTickService.cs` 的 `DrawEvent` 改为实例方法。保留其余并行修改，随后 Unity 编译恢复。

## 接入约定与剩余边界

- 业务继续注入 `IGameRpcSession`，调用 BindService/BindEntity 并拥有返回租约；集合在对象构造时显式 new。
- Host/Client 都须更新 Core 并重新编织。新的状态头不兼容旧 SyncVar payload。
- 本轮证明 Unity Editor/Mono 实际编织、真实 sidecar 通信、三集合/标量 Hook、主线程和 world 生命周期闭环。
- 没有自动把背包/任务/解锁等业务改成新集合；由对应业务 Agent 按 [集合指南](sync-collections-guide.md) 接入。
- 完整游戏 Play、Player、IL2CPP/裁剪、远程网络及 Unity GC 帧耗时仍未验证；不能把 .NET 的 944 B 数字当作 Mono 实测。
