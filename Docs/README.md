# BITKit Multiplayer 文档

**2026-10-03：手写 design-v1（v1～v5）与 design-v2 的 .NET 新链路已实现。** 优先读 [实现和使用](design-implementation.md) 与 [验收](design-implementation-validation.md)，运行 Samples/NetRpc。下方 Unity/B6 文档描述兼容 backend。

**新 NetRpc 也已接通 Unity 2022.3.62f3 Edit Mode**：原生 DI 代理、实际 ILPP、公开 Unity Session、TCP+UDP、主线程与窗口/reload 退出均通过。见 [接入和证据](unity-netrpc-validation.md)。Player/IL2CPP 与完整游戏迁移仍待推进。

**性能优化与默认 UniTask 已整合主源码工作树**：移除旧 adapter 后当前 .NET 回归 193 通过、1 个可选 benchmark 跳过；合入后的 Unity 编译和 smoke/lifecycle 记录仍保留。Scalar RPC 约 96 B/次（独立 .NET 全线程合计），Unity GC 未测。[本次整合](netrpc-main-integration.md) · [默认 UniTask 与 API 升级](netrpc-unitask-default.md)。

**纯 .NET、按房间作用域绑定的 RPC/SyncVar 库。当前已接通 Unity Package、实际 ILPP 和 Edit Mode Host/Client RPC/UDP 窗口。** [打开和使用窗口](unity-editor-probe.md)。Player、完整主线程适配和 AOT 仍是后续工作。

源码核对日期：2026-09-28。最新完整 .NET 验收检查点：2026-09-27、182 项测试通过。这里是当前使用入口；历史里程碑中的协议和性能数字不代表当前实现。

## 从哪里开始

**想先看懂新 NetRpc 的业务写法：打开 [给人类看的 Godot 商店＋靶子](../Samples/NetRpcGodot/HUMAN-START-HERE.md)，运行根目录 `Start-Godot-Human-Lab.cmd`。** 按六个中文按钮逐项核对原始设计，再读接口、Host 实现和 Godot 消费者。

| 目标 | 阅读入口 |
| --- | --- |
| 手写 design-v1/design-v2、原生 DI/RPC/ECS/Relay | **[新链路](design-implementation.md)** → [验收](design-implementation-validation.md) |
| 新对话接手 / AI 开发 | **[AI 接入导航](ai-integration-handoff.md)** → [当前状态](current-status.md) → 任务对应页面 |
| 编写普通 C# RPC | [快速开始](getting-started.md) → [API 与内存契约](api-contracts.md) |
| 集合同步 / Hook / 独立 .NET 示例 | **[SyncVar 集合与 Hook](sync-collections-guide.md)** → [首版验收](sync-collections-validation.md) |
| 在 Unity 里先试 RPC/UDP | **[Edit Mode 双端窗口](unity-editor-probe.md)** → [后续接入手册](unity-integration-plan.md) |
| 理解生成入口、数值 ID、void 语义 | [强类型 RPC](typed-rpc-guide.md) |
| 更换 UDP 实现 / DI / 连接 | [ITransport](transport-guide.md) → [Relay](relay-guide.md) |
| 查看 GC 结论与重现 | [最新性能与验收](typed-rpc-validation.md) |
| 新 NetRpc 默认 UniTask / 最新性能与主工作树整合 | [默认异步栈](netrpc-unitask-default.md) → [主目录 / Unity 整合读回](netrpc-main-integration.md) |
| 查过去的决策和实验 | [历史索引](history-index.md)，按需阅读，不从这里开始 |

## 当前实现的核心事实

- Host 与 Client 互斥；玩家 Host 与 Dedicated Host 都只具有 Host 角色。
- 普通 woven RPC 使用构建期 **Mono.Cecil** 生成的强类型参数读写和直接接收器；没有每次 RPC 运行 Weaver。
- 正常生成调用使用 **B6/v4** 数值包头和 schema 指纹。状态、控制、Task reply 与显式反射兼容入口仍使用 B5/v3。
- `void` 是单向通知；`Task` / `Task<T>` 才等待执行完成或结果。
- Host 权威的三种同步集合、Hook/Changed 和指纹化快照/增量已合入 master 工作树，Unity Edit Mode 真实游戏会话验证通过；旧状态格式需升级并重新编织。见 [接入记录](unity-sync-collections.md)。
- 新 NetRpc 的 Direct 与 Relay 均使用自研 TCP+UDP Transport；可靠帧和已认证的不可靠数据共享同一准入生命周期。
- 借用内存要活到真实消费/发送结束，不能因池化而提前归还。
- 0 B 的断言只覆盖预热后的特定同步生成调用测试，不是整个 Arena 或 Unity 的性能承诺。

## 文档权威与维护

架构约束以 [architecture.md](architecture.md) 为准；当前状态、API 和接入手册描述当前可用行为；验收页说明证据范围。发生冲突时先核对指定源码与测试，再更新当前页，不能从旧日志恢复已经废弃的语义。

[完整目录](SUMMARY.md) · [文档维护与 GitBook](documentation-workflow.md)
