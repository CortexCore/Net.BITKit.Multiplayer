# Unity IL Wrapper / RPC 分阶段接入手册

## 新 NetRpc 当前入口（2026-10-03）

手写 design-v1/design-v2 的新 MessagePack backend 先读 [Unity 新 NetRpc 接入与验收](unity-netrpc-validation.md)。实际 Project B **Unity 2022.3.62f3 Edit Mode** 已通过原生代理生成、assembly opt-in ILPP、公开 `UnityNetRpcSession`、真实 TCP+UDP smoke、Task/UniTask/状态/主线程、窗口关闭/连接与队列清理、重启和 Ready 会话实际域重载。下面 2026-09-28 的 P0～P5 表仍是历史兼容 B6 backend 路线，不能混为新链路进度。

随后性能/默认 UniTask 工作线已整合主目录，合入后的编译、原生 UniTask proxy/receiver/transport、公开 Session 与 adapter/lifecycle 重新读回通过。[整合证据](netrpc-main-integration.md)。Core/Transport/Unity adapter 显式引用 UPM UniTask 2.5.10；不要再向宿主导入同名 NuGet UniTask DLL。主线程和跨 await 身份规则见 [默认栈契约](netrpc-unitask-default.md)。

| 新链路阶段 | 当前状态 |
| --- | --- |
| N0：宿主与依赖 | Project B 2022.3.62f3 / Edit Mode，实际编译与 DLL 引用已确认 |
| N1：生成 / ILPP | 显式 NetRpcBackend opt-in、生成原生接口源码与实际 receiver 已验证 |
| N2：新 backend sockets / 功能 | 同 Editor 独立 Host/Client Runtime，真实 TCP+UDP 的 RPC/ECS/接口状态通过 |
| N3：主线程 / 最小生命周期 | Session Pump、await/Changed 主线程、queue/worker/request 清理、窗口重启与 Ready reload 通过；完整游戏/场景租约仍待接入 |
| N4：跨进程 / Player / AOT | 未验证 |
| N5：Project B 业务迁移 | 旧 GameRpcSession/Observation 已删除。业务类门/子弹保留新 `[Rpc]`；新增账号票据准入与 Direct 物理 Session，仍无游戏世界/Client 生命周期与业务目标接线 |

新会话 Send 是拥有副本后的同步排队提交；await Session.Disposal 才是连接与出站 worker 退出屏障。每 dispatcher 的 32 transport admission 和更多队列/业务 lifetime 约束见新验收页。

## 历史兼容 backend 路线

**2026-09-28 更新：最小 Package/ILPP/Edit Mode 双端通信已接通。** 已验证范围见 [窗口与读回记录](unity-editor-probe.md)；下面保留完整路线，但未完成的关卡不能由窗口测试替代。

先读 [当前状态](current-status.md) 和 [API 契约](api-contracts.md)。目标是复用已验证的 typed RPC 核心，完成几个真实 Unity 编译的 IL Wrapper/RPC probe，然后才迁移业务。

## 阶段门槛

| 阶段 | 交付 | 当前状态 |
| --- | --- | --- |
| P0 | 确认 Unity 项目、版本、指令及 MCP；确认工作树 | 已确认 Project B / 2022.3.41f1 / Edit Mode |
| P1 | Runtime/Transport/Editor 程序集与依赖可导入 | 已在当前宿主依赖环境导入并编译 |
| P2 | 实际 Unity ILPP + 纯服务 void/Task/SyncVar probes | Add/Read/Pose、标量 SyncVar、三种集合及 Hook 已通过实际 Unity ILPP/Game Session Smoke |
| P3 | 有界主线程调度、场景/scope 生命周期、UDP/Target probes | Project B 的 GameRpcSession 主线程 wire、服务/组件租约和 world 清理已接线并在 Edit Mode 读回；完整场景 Play/Target 验收待做 |
| P4 | 实际 Player，包含 IL2CPP/裁剪与性能读回 | 待进行 |
| P5 | 小范围接入 Project B 业务，逐步替换旧发布入口 | 待进行 |

不能因 P1 没有编译错误就标记 P2 完成；不能因 Editor Play 正常就标记 P4 完成。每阶段新增独立证据记录，并更新本表及 current-status。

## P0：首先确认现场

1. 核对独立库和目标 `Com.Project.B.Unity` 的实际路径与 `git status`，不覆盖现有用户改动。
2. 读目标 Unity 根 AGENTS，按要求读取 `.agents/` overlay、codebase workflow 与 Unity MCP domain skill；历史聊天不代替仓库指令。
3. 通过项目 MCP 读 editor state、项目标识/路径、当前场景/编译情况；确认不是连接了另一 Unity 项目。历史记录是 2022.3 系列，实际版本以读回为准。
4. 记录现有 ILPP 和依赖程序集，先找重复 Core/Cecil/MemoryPack/DI，再选择导入路径。

外部 .cs/asmdef/资产修改后，执行目标仓库要求的 MCP recompile/import/reload recovery。当前 Project B codebase workflow 要求 Agent 仅使用 Edit Mode 验证，不主动进入 Play；完整游戏/Player 验证留给用户或后续明确批准的流程。

## P1：包、程序集与依赖

本次已采用 `Src/` 源码 UPM，Editor 编织器单独隔离。新宿主仍需准备对应第三方依赖；不能同名源码和 DLL 重复导入。

| 层 | 当前文件/缺口 | 接入要求 |
| --- | --- | --- |
| Core | 显式引用 MemoryPack.Core / MessagePack / MessagePack.Annotations / Microsoft.NET.StringTools / DI.Abstractions / Unsafe | 保持无 UnityEngine 引用；新 NetRpc 的 MessagePack 3.1.8 已在 Project B Unity 2022.3.41f1 编译和加载读回 |
| Native UDP | 已有 `Net.BITKit.Multiplayer.Transport` asmdef | 引用 Core，数据面可替换 |
| TouchSocket adapter | 已引用 Core/Transport 和显式 DLL | 复用当前 TouchSocketLab 插件，不重复安装 |
| Editor transform / ILPP | `Src/Editor/CodeGen` 已共享变换 API | 目标 Cecil 元数据导入；源码 PDB 行映射仍待完善 |
| Probes | `Src/Unity/Probes` 与 `Src/Editor/Probe` | 已有 Add/Read/Pose，不依赖业务场景 |

NuGet csproj 的 PackageReference 不会自动为 Unity 安装依赖。当前 Core 使用 MemoryPack.Core 1.21.4、DI.Abstractions 8.0.2；TouchSocket.Dmtp 4.3.9；**Core 已不需要 Newtonsoft**。逐项核对实际 netstandard2.1 资产和依赖闭包，防止导入 System.Memory/Unsafe 等与 Unity 内置程序集冲突。

区分 asmdef 的 `references` 与 DLL 插件的 `precompiledReferences`/自动引用设置；不要把 NuGet DLL 名字直接当作 asmdef 名称填写。运行时代码和工具代码的语言版本也不同：CodeGen 项目目前是 `LangVersion=latest`，提取到 Unity Editor 时需要同时核对语法支持，不能只修改目标框架。

MemoryPack 的 DTO Source Generator 在 Unity 编译器中的加载方式、版本兼容和生成代码要单独确认。首个 probe 只用 int/float/固定结构体，可把 reference DTO 的生成验证安排在基础链路之后。`Src/package.json` 的 unity 字段不证明上述事项已经完成。

## P2：真正运行 Unity ILPP

### 变换器如何复用

当前 CLI 仍保留 `Weaver.Weave(inputPath, outputPath)`，实现已提取至包内 `Src/Editor/CodeGen/Weaver.cs` 的共享 `WeaveModule`。Editor adapter 不启动 net10 进程，直接处理 Unity 编译内存模块与引用。

ILPP host 不能反射加载目标中的 ReadOnlySpan 等类型，桥接类型/方法通过目标 Core 程序集的 Cecil 元数据解析。Unity Bee 要求 PE/PDB 成对输出；当前写覆盖全部 MethodDef 的 hidden Portable PDB entries。零行空 PDB 已确认会在 Mono 捕获异常时原生崩溃，不可使用。源 sequence points 尚未恢复；见 [PDB 安全修复](unity-pdb-safety.md)。

检查点：

- 使用目标 Unity 的实际编译引用/resolver；不把工具主机的 System.Private.CoreLib/net10 引用灌入 Unity 程序集。
- 明确 Mono.Cecil 版本与 Editor 已加载副本，不因两个 Cecil 版本导致 adapter 根本不加载。
- 保留/正确处理 PDB；不能写入与新 IL 不匹配的旧符号。当前 CLI 丢符号的行为不应直接当作 Unity 调试方案。
- 保留原方法 token、参数、异常处理及 async 状态机关系；避免反复处理已编织输出。
- 首先只处理 probe 白名单或明确依赖 Core 的目标程序集，不编织框架/依赖/编织器自己。
- 历史 spike 对 `Unity.*.CodeGen` 程序集命名/发现有过敏感性；这只是排查线索。必须记录实际 processor 被发现和执行的证据。

### 第一批实际功能

先用普通 C# 服务，不在接收体中调用 UnityEngine 对象。计数器使用 Interlocked 或明确串行执行，避免把业务数据竞争误判成 RPC 丢失。

| Probe | 验收 |
| --- | --- |
| U1：`[Rpc(Host)] void Add(int)` | Client 调用只在 Host 业务体执行一次；有线通知，没有成功回复/等待器；接口与具体实例效果相同 |
| U2：`[Rpc(Host)] Task<int> Read()` | 返回 Host 值；远端异常、断线、超时结束等待，不伪装成 void |
| U3：`[SyncVar] int Health` | Host 写入到 Client、相同值不重复通知、Client 写拒绝、迟绑定获得当前值 |

每个 probe 需要三种证据：**Unity 编译后的 IL/生成 marker 读回、调用计数/状态结果、相关错误与生命周期断言**。检查 wrapper/receiver 是 typed 调用，没有生成 object[]/参数 box/MethodInfo.Invoke；SyncVar 和冷路径单独记录，不能把整个程序集宣称为无反射。

DI 解析接口和实现必须得到同一个 scoped 对象。两个 Runtime 的相同 TargetKey 不串房间。Host 与 Client 互斥；这个设计不需要 NetworkBehaviour 或另一个隐含本地 Client。

## P3：主线程和生命周期，再触碰场景对象

Core 的接收回调及用户 handler 目前可以在网络线程执行，状态 Apply 也可能从该路径进入。Runtime 锁只保护记账，**不使 UnityEngine 调用线程安全**。

主线程边界至少覆盖：RPC receiver 业务体、SyncVar raw setter/通知、成员/连接回调触发的场景操作，以及业务 await 后继续访问 Unity 对象的路径。

队列须有条数/字节上限；队列项携带 scope epoch、target 身份和可复核的绑定 token。进入主线程后再次检查对象仍绑定、成员仍 ready、未出现 tombstone；场景退出后丢弃旧 epoch 的任务。

**不能把借用的 ReadOnlyMemory 或 ref struct Reader 直接放进队列后让网络 callback 返回。** 可在 callback 内完成解码为独占命令，或复制/保留一个拥有的 pooled frame，到主线程消费完成后归还。异步处理的租用必须活到真正结束，不能为了 0 B 提前释放。

此阶段加入：

- U4：Host All，在 Dedicated/玩家 Host 均本地一次、每 Client 一次；Client 发 All 被拒绝。
- U5：Target 转发、真实 sender/owner 权限；未绑定/已移除对象拒绝。
- U6：Unreliable 的固定结构体位姿、乱序/丢包恢复、UDP 暂停与保身份重绑。
- U7：销毁对象、卸载场景、退出 Play、重建房间及 domain reload 后不执行旧回调，队列/租用恢复。
- U8：在主线程 adapter 验证后，才让接收体驱动一个可见 Transform；记录接收执行线程，不只观察“看起来动了”。

已经开始的远端业务不能因本地等待取消而自动回滚。若业务需要取消，另设计房间生命周期 token 的注入/观察方式。上方新 NetRpc 现已端到端支持 UniTask 并完成合入后的 Edit Mode 生命周期验证；woven CancellationToken 参数仍需遵循签名诊断。以下旧 B6 路线的历史限制不能套到新 backend，Player/AOT 与全游戏生命周期仍是单独关卡。

## P4：Player、IL2CPP 和性能

- 私有 `__bitkit_recv_*` / `__bitkit_body_*` / `__bitkit_set_*`、Bind 时的反射/Delegate.CreateDelegate、MemoryPack formatter 与相关泛型实例可能受裁剪/AOT 影响；设计生成注册或精确保留规则并用 Player 验证。
- 不把 .NET DispatchProxy 兼容 API 作为第一个 IL2CPP 路径；它与普通已生成 handler 不是一回事。
- 测试真正的独立 Host/Client Player，至少覆盖 U1–U7；Editor 双 Runtime 不能代替不同进程。
- 分开测网络热路径、状态/Task/变长 DTO、诊断与显示；禁用/隔离持续磁盘报告。记录 Player/架构/backend、GC Alloc、暂停及帧时间尖峰。
- .NET fake wire 的 0 B、ArrayPool 的可回收，以及本机网络测试都不能自动转写成 Unity/IL2CPP 性能成功。

## P5：Project B 小步迁移

以下是历史导航提示，下一轮先用目标仓库工具确认实际路径/职责：

| 旧区域 | 接入边界 |
| --- | --- |
| `Multiplayer/NetworkWeaveRuntime.cs` 原型 | 用明确 scoped Runtime/实例绑定替代，不恢复 static current-room |
| `ProjectBObservation/Editor/Weaver` | 调查现有 processor，避免与新 ILPP 双重编织 |
| 房间 admission / player identity | 接入真实认证后的 Peer 目录，PlayerId 不等于连接 ID |
| NetworkGameObject / prefab roster | 留作 Unity 对象生命周期适配；先挑一个目标，不全量替换 |
| 旧字段更新 / ECS UDP | 明确唯一发布源，避免同一状态由新旧通道同时写 |
| 账号/商城/代币接口 | 原业务后台保持独立，不因游戏 RPC 接入改造后端协议 |

## 每阶段怎样留下证据

新建对应 Unity 验收页，记录实际版本、场景/程序集、MCP 读回、已运行的 EditMode/Play/Player、IL 片段、日志路径和失败修复。更新阶段表及 current-status；不要勾选未执行的关卡。GitBook 只发布文字记录，本机 trace/截图路径不等于已上传附件。

下一对话启动模板见 [AI 导航](ai-integration-handoff.md)。
