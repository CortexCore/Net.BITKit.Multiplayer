# 当前状态

## 2026-10-07：唯一 NetRpc 后端

旧 RpcRuntime/B4/B5/B6、MemoryPack 协议、room wires、packet transport factory、旧同步集合和混合编织已删除。
当前运行时为 RpcContextService；传输为 native TCP+UDP Direct/Relay 和独立 LiteNetLib Direct 扩展。

普通 RPC 对象使用标准 AddSingleton；AddNetRpcObject、NetRpcBackend 选择属性及旧 Target 路由已删除。
CodeGen 默认编织 NetRpc，Unity ILPP 自动处理 Rpc，不需要额外标记。保留当前 IRpcContext<T>/IDisposable
作者约束、接口服务 AddNetRpcService、Client AddRemoteInterface 以及 Task/ValueTask 互操作能力。

Core 仍为 netstandard2.1 / C#9 / 无 UnityEngine；UniTask 2.5.10、MessagePack 3.1.8、DI 8.0.2。
Core 不再依赖 MemoryPack。Unity 和 LiteNetLib 为独立同级仓库，UPM 根均是 Src。

## 本轮证据

- 隔离 worktree 完整 Release 构建：0 警告、0 错误。
- 新后端完整 solution 回归：51 Core + 15 LiteNetLib/wrapper + 12 performance guard，**78/78**。
- 无后端选择标记的真实 DLL，经默认双参数 CodeGen 编织通过；重复编织明确拒绝。
- 实际生成／编织 Direct 和 Relay Sample 均 PASS：结果 42、生命值 85、集合状态和广播一致。
- 性能工具原来的 IRpcContext 注册缺口已修复；真实 direct scalar 5 次操作的启动／完成 smoke 通过。
- Unity 2022.3.62f3 Edit Mode 已实际解析隔离包路径并编译通过；反射读回旧 Runtime/选择属性不存在、Unity roster 有生成接收器。
- SDK TCP/UDP smoke、adapter 借用／有界队列／关闭测试、窗口与连接退出生命周期均 PASS。
- 宿主 NPC 已迁为 NetComponent 快照和 DI 别名；实际 Host/Client Health=73、IsDead=true、Changed=1、相同值抑制及 Client 写拒绝均通过。
- netstandard2.1 Transport 独立回归 **21/21**。最终证据见 [清理记录](legacy-backend-removal.md)。
- 三个仓库已合并到 main；主目录 Release 构建、恢复原 Unity 包路径后的编译与 TCP/UDP smoke 均再次通过。

## 导航与边界

[用法](../README.md) · [架构](architecture.md) · [API](api-contracts.md) · [Unity](unity-integration-plan.md)

历史设计／实验页描述删除前实现，不作为当前接入路径。完整 Unity Player/IL2CPP、公网/NAT/长时间负载
仍需各自验证；本轮 .NET / Editor 证据不外推这些平台。
