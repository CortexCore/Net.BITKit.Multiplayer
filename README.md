# BITKit Multiplayer

**Godot 2D 同步测试场已可运行：** 双击 `Start-Godot-Sync-Lab.cmd`，或 `powershell -File Samples/NetRpcGodot/Start-Lab.ps1 -Relay`。独立 Host、两个真实 Godot C# Client，移动/攻击/拾取、网络故障、重连均已通过。[使用](Samples/NetRpcGodot/README.md) · [跨进程验收](Docs/godot-sync-lab-validation.md)。移除旧 adapter 后的最新完整 suite **193 通过 / 0 失败 / 1 项可选性能测量跳过**。

纯 .NET、DI/房间作用域驱动的 RPC 与状态同步。**手写 design-v1（v1～v5）与 design-v2 的新 NetRpc 链路已实现**：MessagePack、生成远程接口、普通类 IL Wrapper、TCP+UDP、原生 Relay、ECS 与接口标量/列表/字典。业务由 DI 和 Entity 生命周期自动接线。

新入口：[实现与使用](Docs/design-implementation.md) · [逐项验收](Docs/design-implementation-validation.md) · `Samples/NetRpc`。下方 Unity/B6 记录属于兼容 runtime 历史。

当前完整 Release 构建通过；solution 测试 **193 通过 / 0 失败 / 1 项可选性能测量跳过**，Direct 与 Relay Sample 均 PASS。

**当前阶段：Unity 2022.3 已导入 Package，并在 Edit Mode 接通实际 ILPP、Host/Client RPC 与 UDP 位置同步。** [窗口使用与最小验收](Docs/unity-editor-probe.md)。

**Host 权威的 SyncDictionary / SyncList / SyncHashSet、Hook 与指纹/增量恢复已合入 master 工作树，并通过 Unity Edit Mode 实际会话验证。** [使用指南](Docs/sync-collections-guide.md) · [主库/Unity 接入记录](Docs/unity-sync-collections.md)。仓库尚无初始提交，本次未生成 merge commit。

## 文档入口

**LiteNetLib Direct 已拆为独立可选扩展仓库**：同级 `Net.BITKit.Multiplayer.LiteNetLib`，UPM 包根为 `Src/`，.NET 工程位于该仓库根目录。Core/native Transport 不依赖扩展；构建本仓库完整解决方案、Godot 样例或 LiteNetLib 集成测试时需要同级扩展 checkout。[安装与接线](Docs/litenetlib-guide.md)。

- **[文档首页](Docs/README.md)** / [GitBook 目录](Docs/SUMMARY.md)
- **[AI 接入导航](Docs/ai-integration-handoff.md)**：新对话先读这里，按任务定点查看代码。
- **[当前状态](Docs/current-status.md)**：协议、依赖、完成范围和实际缺口。
- **[Unity 接入手册](Docs/unity-integration-plan.md)**：从程序集/ILPP 到首批 RPC probes、主线程和 Player。
- [快速开始](Docs/getting-started.md) / [API 与内存契约](Docs/api-contracts.md)
- [最新 .NET 验收与 GC](Docs/typed-rpc-validation.md) / [历史索引](Docs/history-index.md)

## 兼容 backend 核心语义

- Host 与 Client 互斥，Dedicated Host 和玩家 Host 使用相同权威角色。
- 普通 woven Reliable/Unreliable 调用使用 **B6/v4** 数值头、schema 指纹和直接 handler；`void` 不等待应用层成功回复。
- Task/Task<T> 保留完成/结果语义；状态、控制和 Task reply 等仍使用 **B5/v3**。
- ITransportFactory 可 DI 替换；默认 native UDP。握手身份来自可靠会话，UDP 端点 proof/鉴权仍保留。
- 生成调用使用借用/池化缓冲；动态 Host 协议补表、Unity ILPP/调度/AOT 等边界以当前状态页为准。

## 构建与样例

在此独立仓库使用 .NET 10 SDK：

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build -- --relay
```

工具/测试用 net10，Core 保持 C#9/netstandard2.1；.NET 10 工具不是 Unity Runtime 插件。Godot 可视化入口见 `Start-Godot-Sync-Lab.cmd`。

历史 .NET 检查点：**182 项通过**；新链路及最新完整结果见 [本轮验收](Docs/design-implementation-validation.md)。预热固定类型 void 发送有 0 B 断言，不意味着真实网络或 Unity 零 GC。旧 backend 的 Unity Edit Mode 已有记录，新 backend 尚无 Unity/Player/IL2CPP 验收。

集合首版及后续优化的历史数字见 [集合 GC 记录](Docs/sync-collections-gc.md)。当前回归由主测试项目和原生 NetRpc Direct/Relay 测试覆盖；Player/AOT 仍未验证。

后续 [集合 GC 优化](Docs/sync-collections-gc.md) 已完成：Core **93/93**、GameTests **30/30**，真实 TCP 单项 int 更新约 **8208→944 B**；不宣称整个网络或复杂 DTO 零 GC。

项目/程序集前缀：`Net.BITKit.Multiplayer`；命名空间：`BITKit.Multiplayer`；UPM 身份：`net.bitkit.multiplayer`。当前包使用宿主已安装的 UniTask、MemoryPack、MessagePack、DI 和 Editor 编译依赖；不是自包含第三方依赖的安装包。

`.gitbook.yaml` 已配置 `Docs/` 为 GitBook 内容根；文档源和 AI 导航共用仓库 Markdown。[维护/发布说明](Docs/documentation-workflow.md)。
