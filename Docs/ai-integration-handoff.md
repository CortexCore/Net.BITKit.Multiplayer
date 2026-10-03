# AI 接入导航：先读这里

目标：让新对话先确定当前状态，再定点读代码；不要重读整仓或全部历史实验。**最小 Unity Package/ILPP/Edit Mode RPC/UDP 已接通，先从现有双窗口继续开发。** [窗口记录](unity-editor-probe.md)。

**新 NetRpc Unity 续接（2026-10-03）**：先读 [新 backend 验收](unity-netrpc-validation.md) 与 [进度交接](unity-netrpc-progress-handoff.md)。Project B 2022.3.62f3 Edit Mode 的实际 ILPP、原生代理、公开 Session、真实 TCP+UDP、主线程和窗口/Ready reload 生命周期已通过；菜单为 `Tools/BITKit/NetRpc/Open Unity Sync Lab`。下面旧 B6/TouchSocket 窗口与协议误判说明属于兼容 backend。Runtime/Transport 热点优化在独立工作线，本 Unity 接入不要覆盖其性能改动。

**后续整合完成**：性能 worktree 的优化和默认 UniTask 已合入主源码工作树，Unity adapter/Session/probe 同步迁移。先读 [主工作树整合证据](netrpc-main-integration.md) 与 [默认 UniTask](netrpc-unitask-default.md)，保留无 HEAD / 未提交的现状；不要从优化 worktree 整目录覆写主目录的新 Unity 接入。

**Project B 阶段交接（2026-09-29）**：若接续游戏联机工作，先读同级 `Com.Project.B.Unity/Docs/multiplayer-phase-handoff-2026-09-29.md`。它汇总已接入的实体/Ownership、Client 光标修复、6C proxy 动画与插值修复、跨仓工作树边界及下一次验证顺序；不要把已有进展误当仍停留在双窗口原型。

## 每次开工

1. 读目标仓库 `AGENTS.md`，查看工作树状态，尊重用户已有改动。
2. 读 [current-status.md](current-status.md)。它区分当前实现、.NET 证据和未完成的 Unity 工作。
3. 按下表选择一条路线，再阅读列出的源码符号。不要默认全读 2,000 行 RpcRuntime。
    接续手写设计时读 [新链路](design-implementation.md) 和 [验收](design-implementation-validation.md)。用户要求 design-v1（含 v3）与 design-v2 全部完成；[旧 v2 Agent 交接](design-v2-agent-handoff.md) 仅是历史进度，不继承它的范围限制。
4. 要改架构/语义时再读 [architecture.md](architecture.md)；操作接口看 [api-contracts.md](api-contracts.md)。

## 按任务定位

人类第一次阅读新 NetRpc 或手写 Godot 业务：先读 `Samples/NetRpcGodot/HUMAN-START-HERE.md`，入口 `Start-Godot-Human-Lab.cmd`。`Contracts/IWorkshop.cs` → `Game/Workshop.cs` → `Godot/HumanView.cs` 的前半部分；`Game/TrainingDummy.cs` 展示普通类 Rpc/ECS，`Session/HumanSetup.cs` 仅负责启动。真实双 Godot 的聚焦证据在案例页。不要让初学者先读故障注入、自动验收或 Runtime 内部。

新 NetRpc 的可视化 / Godot 验证入口为 `Samples/NetRpcGodot` 与 [Godot 验收](godot-sync-lab-validation.md)：纯 Game 编织、Session 原生代理、独立 Host/Relay、两个实际 Godot Client。不要改用旧 Arena/B6 backend，或用 Godot 内置 RPC 替代这项新链路验收。

LiteNetLib 并行实现按 [专用 Agent 交接](litenetlib-agent-handoff.md)：先核对当前无 HEAD/未追踪文件的共同基线，再开隔离工作区。该 Agent 负责 Direct Transport，主 Agent 负责 GC 基线/Runtime，首版不包含 LiteNetLib Relay。

| 任务 | 当前页面 | 首先查看的文件/符号 | 对应验证 |
| --- | --- | --- | --- |
| 新 NetRpc Unity 主线程/生命周期 | [新 backend 验收](unity-netrpc-validation.md)、[进度交接](unity-netrpc-progress-handoff.md) | `Src/Unity/NetRpc/UnityNetRpcSession.cs`、`UnityNetRpcDispatcher.cs`；`Src/Editor/NetRpcProbe/UnityNetRpcLab.cs` | BeginSmokeReadback / BeginLifecycleReadback 异步启动后轮询；Ready reload 的 SessionState 证据；不阻塞 Editor 等待 smoke |
| Unity ILPP / IL Wrapper | [Unity 手册](unity-integration-plan.md) | `Src/Editor/CodeGen/Weaver.cs`: `WeaveModule`, `MoveBody`, `EmitTypedWrapper`, `EmitTypedReceiver`; `TypedRpcILPostProcessor.cs` | 已有真实 Unity probe；修改后做最小编译/调用读回 |
| Unity 包与依赖 | Unity 手册 P1 | `Src/package.json`, 各层 asmdef, `Projects/*.csproj`; 宿主 `Assets/packages.config` 和 TouchSocketLab Plugins | 现有宿主已导入；新宿主仍需安装依赖 |
| Edit Mode 双端窗口 | [窗口用法](unity-editor-probe.md) | `Src/Editor/Probe/ProbeNode.cs`, `ProbeWindows.cs`, `Src/Unity/Probes/RpcProbeService.cs` | Add/Read、真实 UDP 暂停恢复；不进入 Play |
| 主线程/生命周期 | Unity 手册 P3、[内存契约](api-contracts.md) | `RoomMemoryContracts.cs`; `RpcRuntime.ReceiveMemory`, `ReceiveTyped`, `RunTypedLocal`, `Apply`, `Dispose` | 帧队列、过期 scope 丢弃、销毁/退出清理、主线程断言 |
| Project B 领域业务接入 | API 契约的 Project B 入口；宿主 `Docs/bitkit-multiplayer-editor-probe.md` | 宿主 `IGameRpcSession.cs`, `GameRpcSession.cs`, `GameRpcObjectBindings.cs`; HostObservationService / ClientMode 生命周期 | 注入会话、显式权限、绑定租约；不要自己开连接或从 Editor probe 拿 Runtime |
| RPC/上下文/权限 | [typed 指南](typed-rpc-guide.md) | `Contracts.cs`, `TypedRpc.cs`; `RpcRuntime.BeginTyped`, `SendTypedVoid`, `SendTypedTaskCore`, `RunTypedTaskReceiver` | `Tests/TypedHotTests.cs`, `RuntimeTests.cs` |
| Unity 对象 Ownership / 本地控制归属 | [NetworkBehaviour](unity-networkbehaviour.md)；宿主 `Docs/network-entities-service.md` | Core `INetworkOwnership`；Unity NetworkBehaviour；宿主 UnityNetworkEntitiesService.SetOwner / NGO3 roster | 三端 Game Session Smoke、NetworkOwnershipRosterTests；Bind(owner) 仍是私有 RPC/状态过滤，不等于对象 Ownership |
| 数值 ID/Schema | typed 指南、[DTO](reliable-binary-guide.md) | `CodeGen.SchemaIdentity`; `TypedRpcHeader`, `TypedTarget`; `RpcRuntime.Bind`, `ReceiveTyped` | `CodeGenRegressionTests.cs`、指纹/目标歧义测试 |
| Native / Relay / DI | [Transport](transport-guide.md)、[Relay](relay-guide.md) | `TransportContracts.cs`, `UdpTransportFactory.cs`, `UdpTransport.cs`, `UdpLane.cs`, `RelayProtocol.cs`, `RelayWires.cs` | `Tests/TransportTests`, `DatagramTests`, `RelayTests` |
| SyncVar / 同步集合 / Hook | [集合指南](sync-collections-guide.md)、[API 契约](api-contracts.md) | `RpcRuntime.Sync.cs`, `SyncCollection.cs`, `SyncList/SyncDictionary/SyncHashSet.cs`, `SyncWire.cs`; Weaver state-list/EmitStateHook | `SyncCollectionTests` 实际编织、Direct/Relay、版本恢复/所有权；独立 Samples/SyncCollections |
| 性能/GC | [隔离网络优化](network-gc-isolated-validation.md)、[typed 验收](typed-rpc-validation.md) | `UdpLane.cs`, `UdpTransport.cs`, `Tests/DatagramTests/NetworkAllocationTests.cs`, `Tests/TypedHotTests.cs`, `Tools/Performance/Program.cs` | 先确认 worktree；同步零分配断言、真实 socket 进程计数与 Arena trace 分开，不混用 |
| 新 NetRpc 默认 UniTask / 性能整合 | [默认栈](netrpc-unitask-default.md)、[主目录整合](netrpc-main-integration.md) | `NetRpcRequests.cs`、`NetRpcV1.cs`、`NetRpcState.cs`、`NetRpcWeaver.cs`、`TcpTransport.cs` | 主目录 .NET 295 / netstandard 4；合入后实际 Unity 编译、smoke、adapter/lifecycle；Player/AOT/Unity GC 仍未验证 |
| 一键样例 | [快速开始](getting-started.md) | `Samples/Arena/Start-Arena.ps1`, `Game/ArenaSession.cs`, `App/Program.cs`, `E2E/Program.cs` | E2E summary、节点 JSON、必要时 PNG 读回 |

定位方法：先查上述符号，再读取其所在方法和调用处；只有出现新的依赖关系才扩展搜索。历史原因按需查 [history-index.md](history-index.md)。

## 不要误判的七件事

1. **RPC 是构建期 Cecil 生成，不是运行时 Weave。** MemoryPack 有 Source Generator；本库尚无 Roslyn RPC generator。
2. **Host 不是 Host+Client 双角色。** All 包括 Host 一次，但不创建隐含 Client Runtime。
3. **B6/v4 是普通生成调用；B5/v3 仍用于状态/回复/兼容 API。** B4/v2 不再是普通 woven UDP 路径。
4. **void 无成功 ACK/等待器。** Task 才等待完成；不要从旧测试或记录恢复 void waiter。
5. **方法表未由 Host 动态下发。** 现在是生成 ID/指纹与本地 descriptor 逐调用匹配；动态补表延期。
6. **0 B 只在特定预热同步测试成立。** 真实网络、DTO/数组、诊断和 Unity 都不能直接继承这个结论。
7. **最小 Editor 成功不代表 Player 完成。** 当前宿主 Package/ILPP/窗口已通过；新宿主依赖、PDB 源行映射、Player/AOT 仍需单独处理。

## 下一轮 Unity 的起点

独立库：`Net.BITKit.Multiplayer`。目标项目：同级的 `Com.Project.B.Unity`。当前开发机常见位置是：

```text
D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer
D:\Iris\Documents\GitHub\Com.Project.B.Unity
```

路径是导航提示，开工先核对实际目录。进入 Unity 仓库后，其根 AGENTS 要求的 `.agents/` 优先级 overlay、project-local skills 和 MCP workflow 继续适用；本页不能代替它们。

先通过目标项目 MCP 确认实际 Editor 与项目；已有验证为 2022.3.41f1。按 [窗口记录](unity-editor-probe.md) 使用现成 Host/Client，遵守用户要求做最小必要读回，不重复跑全套验证。剩余阶段见 Unity 手册，不一次替换全部旧业务网络。

### 可以直接交给下一对话的任务

```text
继续 BITKit Multiplayer 的 Unity IL Wrapper/RPC 开发。
先读独立库 AGENTS.md、Docs/ai-integration-handoff.md、Docs/current-status.md
和 Docs/unity-integration-plan.md，再遵守目标 Unity 仓库的 AGENTS/overlays/skills。
通过 MCP 确认实际 Unity 项目与编辑器状态。
Package/ILPP 和 EditorWindow 的 void Add、Task<int> Read、UDP Pose 已跑通；
先读 Docs/unity-editor-probe.md，从这些现成入口继续实现用户指定功能。
不要无理由重做全量验证；修改后只做对应编译与最小行为读回。
主线程调度验证前，接收业务体不调用 UnityEngine 对象。
保留 Host/Client 互斥、typed B6/v4、void 无回复等待及借用缓冲生命周期。
优先定点读取导航列出的文件，不重扫全部代码或恢复旧的静态 NetworkWeaveRuntime。
```

## 命令与交接

独立库允许常规 `dotnet build/test`；Unity 仓库编译、导入与 Play 验证使用其专用规则，不能把这里的命令套过去。

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --filter FullyQualifiedName~TypedHotTests
```

每阶段交接写明：改了哪个 adapter/程序集、读回了什么实际 IL/对象/结果、运行在哪个 Editor/Player/backend、证据位置、尚未验证的下一关。同步更新当前状态和 Unity 阶段表，不把长聊天记录当作唯一知识库。**不要未经请求 commit/push/publish。**
