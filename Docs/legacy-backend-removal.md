# 旧网络后端删除（2026-10-07）

## 隔离范围

主库、Unity 扩展、LiteNetLib 扩展分别在 `cleanup/legacy-backend` worktree，放于同一父目录并保持仓库名。
其相对 ProjectReference 均指向本组隔离源码；验证前未修改各仓库 main。

## 删除与迁移

- 删除旧 RpcRuntime、B4/B5/B6、typed MemoryPack 编码、旧同步集合、NetworkTime、room wires/hub、UDP packet factory。
- 共享 Contracts.cs 只保留当前使用的 RpcError/RpcException；RpcContracts 保留 Rpc、Delivery、Host/All、接口生成和 WovenAssembly 标记。
- 删除旧编织实现、混合分流和 NetRpcBackend 类型；默认 CodeGen 和 Unity ILPP 统一到 NetRpc。
- 保留新编织器所需的元数据 helpers、BCL 引用归一化和 Unity 安全 portable PDB 支持。
- 删除旧测试／fixture，保留新后端回归，增加默认 CLI／无选择标记与旧 public API 不存在的检查。
- 所有普通对象注册迁为 AddSingleton，删除 AddNetRpcObject；接口服务注册仍负责 native receiver/别名/契约元数据。
- 性能工具 Host 改用 AddNetRpcRuntime，提供 IRpcContext 基础设施而不启动额外后台发布计时器。
- 当前文档导航只展示新后端；历史页保留为日期化证据，不保留旧运行实现。

## 实际验证

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo -m:1
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo -m:1
dotnet Artifacts/bin/NetRpc.Sample/Release/net10.0/NetRpc.Sample.dll
dotnet Artifacts/bin/NetRpc.Sample/Release/net10.0/NetRpc.Sample.dll --relay
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --profile scalar --iterations 5 --warmup 2 --container-size 1 --idle-ms 10 --timeout-seconds 10 --output Artifacts/LegacyCleanupSmoke
```

构建 0 警告 / 0 错误；回归 78/78；两个 Sample PASS。性能 smoke 的 Host+Client 5 次操作正确完成，
报告在 Artifacts/LegacyCleanupSmoke；它证明工具启动与调用接线，不作为新的稳定性能基线。

Roslyn MCP 因服务器 sanctioned-root 拒绝隔离路径；编译证据来自实际 MSBuild。
netstandard2.1 Transport 专项：`dotnet test Tests/NetRpcCompatTests.csproj -c Release --nologo -m:1`，**21/21**。
这里的 Compat 是框架运行兼容检查，测试的是唯一 NetRpc 后端，不是保留旧网络实现。

## Unity 实际读回

宿主 Project B Unity 2022.3.62f3，全部 Edit Mode。Package Manager 先 Resolve，再刷新／域重载，实际路径：

```text
BITKit-Multiplayer-Cleanup/Net.BITKit.Multiplayer/Src
BITKit-Multiplayer-Cleanup/Net.BITKit.Multiplayer.Unity/Src
```

- 编译错误读回为空。旧 RpcRuntime 与 NetRpcBackendAttribute 反射检查不存在，UnityNetworkObjects 有 __netrpc_recv 接收器。
- SDK smoke：原生代理 NetRemote_3844293574；woven=true；Plus=42、HP=80、X=128、List=1、Dictionary[1]=1；All=1/1；异步及 Changed 主线程；权限写拒绝。
- Adapter：借用字节在 poison 后仍正确、队列有界、overflow/late close、在途与队列取消、pending=0、bytes=0。
- Lifecycle：关闭窗口取消业务与 pending 请求、TCP/UDP/proof worker 退出、队列=0、无旧回调、重建 smoke 与初始化期间 Stop 均通过。
- NPC：NetworkNpcState 使用 MessagePack 快照的 NetComponent，NpcFactory 暴露同实例 INetComponent 别名。
  通过实际 TCP/UDP Host/Client 会话注册 NetEntity，读回 Health=73、IsDead=true、Changed=1；相同值不重复通知、Client Publish 被拒绝。

宿主同步迁移包括 4 处原选择标记，以及审查期间新增车辆 RPC 文件中的同一标记；原有其他未提交工作保留。
NPC 验证证明组件契约与传输接线，不代表完整 NPC 世界／地图生命周期已经验收。
未进入 Play，不宣称 Player/IL2CPP 或公网验证。临时宿主包引用待合并／恢复后再次读回。
