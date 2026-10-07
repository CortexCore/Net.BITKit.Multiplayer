# BITKit Multiplayer 文档

当前只有 **NetRpc** 后端，支持 Unity 与 .NET。旧 B4/B5/B6 及其编织／传输实现已经删除。

| 目标 | 入口 |
| --- | --- |
| RPC、Host/Client/Relay、Transport 与 Unity 对象同步 | [项目 README](../README.md) |
| 构建与接口代理生成 | [快速开始](getting-started.md)、[实现与使用](design-implementation.md) |
| 架构与 API | [当前架构](architecture.md)、[API 与内存契约](api-contracts.md) |
| Unity 适配 | [Unity 接入](unity-integration-plan.md)；独立 Unity 扩展仓库 |
| 自建传输 | [Transport](transport-guide.md)、[Relay](relay-guide.md)、[LiteNetLib](litenetlib-guide.md) |
| 可运行业务案例 | [Godot 商店＋靶子](../Samples/NetRpcGodot/HUMAN-START-HERE.md) |
| 清理内容与证据 | [旧后端删除](legacy-backend-removal.md)、[当前状态](current-status.md) |
| AI 接手 | [接入导航](ai-integration-handoff.md) |
| 过去的设计和实验 | [历史索引](history-index.md) |

历史页中的旧 API、文件路径、计数和协议描述仅解释当时的验证，不是当前接入指令。
