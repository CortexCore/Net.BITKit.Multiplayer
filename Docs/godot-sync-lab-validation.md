# Godot 新 NetRpc 跨进程验收

日期：2026-10-03；Godot 4.7.2 复验：2026-10-06。入口：[Samples/NetRpcGodot](../Samples/NetRpcGodot/README.md)。

## 实际环境

- 当前 Godot `4.7.2.stable.mono.official.ed1daf0bf`；通过 `GODOT_BIN`、`-Godot` 或 PATH 定位，不依赖仓库中的绝对路径。
- Client / Game / Session：net8.0，Godot.NET.Sdk 4.7.2。
- Host / Relay / CodeGen / E2E：.NET 10；Core netstandard2.1。
- 使用新 `BITKit.Multiplayer.NetRpc`、MessagePack、原生 TcpTransport / UDP / RelayEndpoint；没有使用旧 Arena/B6/TouchSocket 或 Godot 内置 RPC 代替。

## 最新证据

| 模式 | 证据目录 | 实际 Client PID | 结果 |
| --- | --- | --- | --- |
| 4.7.2 Direct headless | Artifacts/NetRpcGodot/20261006-063331-direct | 61712 / 49812 | PASS |
| 4.7.2 Relay headless | Artifacts/NetRpcGodot/20261006-063353-relay | 34748 / 21116 | PASS |
| 4.6.1 Direct 可见窗口 | Artifacts/NetRpcGodot/20261003-061831-direct | 21360 / 48044 | PASS |
| 4.6.1 Relay 可见窗口 | Artifacts/NetRpcGodot/20261003-061557-relay | 38940 / 4948 | PASS |

已读取可见窗口的实际 PNG，UI 显示玩家 2 的 80 HP、Scores[1]=10、背包一项、UDP loss=25%、乱序与 delta gap 计数。

Relay 最新 Client 1 记录 8 个 UDP 丢弃、13 个重排、1 个已恢复事件集合断档；Client 2 记录 11 个丢弃、17 个重排。两份 JSON 都确认实际 woven 接收器、Host 权威、本地写入拒绝、移动/攻击/拾取、单调 revision。Client 1 确认重连；Client 2 确认旧连接离开和冒用另一槽位被拒绝。两端最终 Health2=80 / Inventory101=1 / Score1=10。

并非所有字段都在每个 Client 执行：Client 1 专门验证重连/集合断档，Client 2 专门验证离开观察/权限冒用；summary 的聚合断言检查对应角色的完整要求。

## 本轮发现及修复

1. Godot 脚本程序集从 bytes 加载，Location 为空。预编译 Factory 优先物理输出，缺少物理候选时允许唯一加载类型，仍拒绝不明确的多候选；实际 Godot 原生代理已调用成功。
2. net8 游戏将 net10 CodeGen 作为显式 build-only 依赖，避免错误的运行时 framework 引用。
3. NetRpc.targets 使用明确的 BaseIntermediateOutputPath/Configuration/TargetFramework 放置生成源码，避免在项目根生成导致重复 Compile。
4. Direct/Relay 统一通过 RpcContextService.PeerDisconnected 清理对应槽位，事件每次真实绑定删除一次，重复 Detach 不再通知；新增定向回归通过。
5. 自动移动使用低幅度输入，避免在延迟/乱序环境中把旧 replica 位置误当 Host 当前位置而越过拾取范围；Host 仍严格执行范围约束。
6. 4.7.2 复验暴露 Host 手写 Runtime 接线漏掉 `IRpcContext<>` 基础设施；改用标准 `AddNetRpcRuntime` 后 Direct/Relay 双 Godot headless 均通过。

## 构建与回归

移除旧 adapter 后，solution Release 构建和 Godot 4.7.2 Debug 项目构建成功。当前完整 suite **193 通过 / 0 失败 / 1 项可选性能测量跳过**。

Godot 项目单独构建：

```powershell
dotnet build Samples/NetRpcGodot/Godot/NetRpcGodot.Client.csproj
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -SkipBuild -Auto
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -SkipBuild -Auto -Relay
```

首次可见 GL 运行出现过一条 Godot GLES shader initialize 报错但仍正常绘制/通过；最新可见轮次日志没有该报错。证据为实际 viewport 和独立进程 JSON，不是模拟 Godot Node 的测试。

验证范围：Windows 本机 loopback 与可控丢包/延迟/乱序，不外推公网、真实 NAT、Linux、Godot 导出包或 Unity Player/AOT。
