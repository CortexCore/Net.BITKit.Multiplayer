# 目录

## 开始

* [文档首页](README.md)
* [当前状态与边界](current-status.md)
* [快速开始](getting-started.md)
* [给人类看的 Godot 商店＋靶子](../Samples/NetRpcGodot/HUMAN-START-HERE.md)
* [AI 接入导航](ai-integration-handoff.md)

## 当前使用与契约

* [架构约束](architecture.md)
* [手写设计新链路：实现与使用](design-implementation.md)
* [手写设计新链路验收](design-implementation-validation.md)
* [Godot 新 NetRpc 跨进程验收](godot-sync-lab-validation.md)
* [LiteNetLib 实现 Agent 交接](litenetlib-agent-handoff.md)
* [API 与内存契约](api-contracts.md)
* [引擎无关网络对象](network-objects.md)
* [网络对象验收与限制](network-objects-validation.md)
* [强类型 RPC](typed-rpc-guide.md)
* [SyncVar 集合与 Hook](sync-collections-guide.md)
* [NetworkTime 与时间 Label](network-time.md)
* [二进制 DTO 与兼容性](reliable-binary-guide.md)
* [ITransport 与原生 UDP](transport-guide.md)
* [Relay 使用](relay-guide.md)
* [实现说明](implementation-notes.md)

## Unity 下一阶段

* [Unity 新 NetRpc 接入与验收](unity-netrpc-validation.md)
* [Unity 新 NetRpc 进度交接](unity-netrpc-progress-handoff.md)
* [默认 UniTask / 热点优化主工作树整合](netrpc-main-integration.md)
* [Edit Mode Host/Client 窗口](unity-editor-probe.md)
* [主库合入与 Unity 集合同步](unity-sync-collections.md)
* [Unity NetworkBehaviour 与角色事件](unity-networkbehaviour.md)
* [Unity PDB 安全修复](unity-pdb-safety.md)
* [Unity IL Wrapper / RPC 接入手册](unity-integration-plan.md)

## 验证与维护

* [GitHub 最新基线整合（2026-10-07）](upstream-integration-20261007.md)
* [隔离网络 GC 优化与健壮性](network-gc-isolated-validation.md)
* [新 NetRpc 真实进程 GC 基线](netrpc-gc-baseline.md)
* [网络模块独立 GC 基准（2026-10-06）](network-module-baseline-20261006.md)
* [新 NetRpc 集合 GC 收敛与限时验证](netrpc-collection-gc-improvement.md)
* [新 NetRpc 实际分配与 CPU 栈热点](netrpc-hotspots.md)
* [新 NetRpc 第一轮热点优化实测](netrpc-hotspots-optimization.md)
* [新 NetRpc 默认 UniTask、升级与端到端实测](netrpc-unitask-default.md)
* [LiteNetLib Direct 使用](litenetlib-guide.md)
* [LiteNetLib Direct 验收](litenetlib-validation.md)
* [SyncVar 集合验收](sync-collections-validation.md)
* [同步集合 GC 对照](sync-collections-gc.md)
* [最新 .NET 测试和分配结果](typed-rpc-validation.md)
* [文档维护与 GitBook](documentation-workflow.md)
* [历史设计与实验索引](history-index.md)
