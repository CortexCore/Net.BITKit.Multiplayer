# 历史设计与实验索引

本页用于追溯，不是开工必读。旧文件中的“当前”“最新”和测试数量只表示当时的检查点；以 [当前状态](current-status.md) 和 [当前 API](api-contracts.md) 为入口。文件留在原路径，避免破坏既有链接。

## 验收与性能沿革

| 检查点 | 记录 | 当时范围 |
| --- | --- | --- |
| 首个 .NET slice | [validation](validation.md) | 36 项，可靠 RPC/SyncVar 基础 |
| Arena V2 | [arena-v2-validation](arena-v2-validation.md) | 55 项，多进程图形 sample |
| V2.1 | [arena-v2.1-validation](arena-v2.1-validation.md) | 72 项，显示插值 |
| 可靠 Relay | [relay-validation](relay-validation.md) | 98 项，反向 Host 与中继 |
| UDP + MemoryPack | [v4-validation](v4-validation.md)、[GC 报告](v4-gc-report.md) | 130 项，旧 V4 检查点 |
| 分配定位 | [performance-gc-attribution](performance-gc-attribution.md)、[TouchSocket 证据](touchsocket-udp-allocation-evidence.md) | 132 项，确认每包 64 KiB 的旧 UDP 热点 |
| Native ITransport | [transport-validation](transport-validation.md) | 154 项，默认 native UDP |
| 可靠 binary v3 | [reliable-binary-validation](reliable-binary-validation.md) | 161 项，去掉 Runtime JSON/JToken |
| typed RPC | [typed-rpc-validation](typed-rpc-validation.md) | **最新检查点：182 项**，直接调用器、单向 void、借用缓冲 |

## 当时的设计/实施计划

- [Arena V2](arena-v2-design.md)、[V2.1](arena-v2.1-plan.md)
- [Relay 设计](relay-design.md)
- [原 UDP 阶段契约](v4-design.md)、[原 V4 使用说明](v4-guide.md)
- [Transport 实施计划](transport-plan.md)
- [可靠二进制实施计划](reliable-binary-plan.md)
- [typed 实施计划](typed-rpc-plan.md)、[当时的核心交接](typed-core-handoff.md)

这些计划可能含后续已经改变的接口、依赖、分工或完成状态；不要将里面的 TODO 当成当前任务，也不要用旧协议绕过当前测试。

## 证据文件

`Artifacts/ArenaRuns/`、`Artifacts/GcRuns/` 中的日志、JSON、trace 和截图是本地生成证据，通常被 Git 忽略，不会随 GitBook 文档空间发布。历史页中的本地路径用于在开发机读回；网站访问者应使用页内记录的命令复现。
