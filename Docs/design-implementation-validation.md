# 手写设计验收记录

日期：2026-10-03。目标：[design-v1](design-v1.md) 全部五阶段和 [design-v2](design-v2.md) 两部分。[实现和使用](design-implementation.md)。

## 定向证据

- NetRpcV1Tests：补表/DI、void、真实 TCP、源码形状。
- NetRpcDesignTests：实际编译的 DI 代理/强类型接收器；Task/ValueTask/UniTask、null、错误、超时/取消/Dispose；字段/集合/ECS；两房间、旧连接回复隔离；Identity、schema/指纹/旧版本；丢包自动恢复、迟加入；容量和非法增量原子性；DTO 副本；借用缓冲立即覆写；TCP 最大帧和真实 UDP 端口重映射。
- NetRpcWeaverTests：实际 Cecil 编织加载 DLL，接口/具体类、嵌套原业务体、Host/All、Task/ValueTask/UniTask、错误、无效声明拒绝生成、接口绑定 woven 实现、预热发送分配断言。
- NetRpcRelayTests：实际 TCP/UDP 两跳、同一 Client API、Direct 独立、Relay 后上线/重启重连、数值 target ID 不误识别为控制帧。
- Samples/NetRpc：构建期代理生成 + 应用程序集编织 + 原生 Transport。Game.cs 无 Bind*、Transport 或 packet 接线。

定向 NetRpc **28/28 通过**。完整构建成功（0 error）；完整 suite **250 通过、0 失败、2 跳过**。两项跳过是原有可选性能测量，不是新功能验收。

| 项目 | 通过 | 跳过 |
| --- | ---: | ---: |
| Core / Net.BITKit.Multiplayer.Tests | 134 | 1 |
| TransportTests | 16 | 0 |
| DatagramTests | 22 | 1 |
| RelayTests | 20 | 0 |
| Arena.AppTests | 19 | 0 |
| Arena.GameTests | 30 | 0 |
| Arena.LobbyTests | 9 | 0 |
| **合计** | **250** | **2** |

最后构建后的 Direct 与 Relay Sample 都再次输出下方 PASS。对 Game.cs 的接线关键词检查无匹配。

## 重现

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NetRpc --nologo
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release --no-build -- --relay
```

样例断言输出：

```text
PASS Direct: Plus=42 Health=85 Items=[10] Counts[10]=1 Broadcasts=2
PASS Relay: Plus=42 Health=85 Items=[10] Counts[10]=1 Broadcasts=2
```

## GC 口径

- WarmTypedMessageBagHasNoPerValueAllocations：预热 1000 次池化 int/float 写入，线程计量 **0 B**。
- WarmWovenTypedVoidSendIsAllocationFreeWithSynchronousTransport：真实 woven 方法、预热 1000 次 int void Client 发送、同步消费 sink Transport，线程计量 **0 B**。
- 这些不意味着真实 socket、Relay、Task 等待器、集合枚举或可变 DTO 全部 0 B。TCP/UDP 验证行为和所有权；UDP managed receive/异步任务仍有分配。

## 回归修复

旧 backend 的 Local_authorization_failure_releases_packet_without_starting_fanout 揭示 Host 本地未执行显式 authorize，已修复；owner-only 仍保留 Host 权威，没有跳过失败断言。

真实 UDP Changed 订阅移到注册/发送前，避免自动同步已提交初值后再等待相同 Changed。

旧 Relay 的 HostDisconnectClosesAdmittedClientExactlyOnce 等待条件包含 PeerLeft 通知本身；IsConnected=false 不代表异步事件已发布。仍断言恰好一次并检查 Dispose，不依赖额外 sleep。

普通类本地路径还验证了 Host 原参数引用语义、All 本地执行一次与出站参数冻结、显式本地拒绝不执行业务体。

MSBuild 每次 Build 从原始 intermediate DLL 重建 woven 输出，避免增量 copy 还原了应用 DLL 而旧 stamp 错误跳过编织；Sample 验收包含连续构建后的实际调用。

## 平台

Windows、.NET 10/8、实际 loopback sockets、生成/编织程序集、UniTask 2.5.10。Project B Unity 2022.3.41f1 的引用/编译/DLL 加载历史检查点已通过 MCP，MessagePack/Annotations 3.1.8.0、StringTools 包 17.11.4，退出 Safe Mode。后续 **2022.3.62f3 新 backend Edit Mode 网络/主线程/Session/窗口与 Ready reload 生命周期验收通过**，独立记录见 [Unity 新 NetRpc 验收](unity-netrpc-validation.md)，本轮聚焦 weaver 回归 4/4。跨 Unity 进程、完整游戏、Player/IL2CPP、Linux或公网仍未验证。端口重映射用真实 UDP socket 模拟，不等于公网/NAT 验收。
