# Godot C# / NetRpc 2D 同步测试场

**第一次阅读/亲手写业务：先看 [给人类看的商店＋靶子案例](HUMAN-START-HERE.md)，双击根目录 `Start-Godot-Human-Lab.cmd`。** 它使用本目录同一新 NetRpc、生成器和编织器，但把业务示例与连接/自动验收分开，不需要先理解下面完整 Arena 的故障注入、身份和重连。

使用 **Godot 4.6.1 .NET**，默认路径：
`D:\Iris\Applications\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe`。

## 一键玩

手写 C# / 人工验证时，打开本目录的 **`NetRpcGodot.sln`**（包含 Contracts、Game、Session、Host、Relay、Godot Client 和 E2E），并用 Godot .NET 编辑器打开 `Godot/project.godot`。

- 网络接口和同步属性：`Contracts/IArena.cs`。
- Host 业务、ECS 组件与 woven RPC：`Game/ArenaWorld.cs`。
- DI、连接与客户端会话：`Session/ArenaSession.cs`。
- Godot 输入、显示与按钮：`Godot/ArenaView.cs`。

修改后运行下面的一键启动命令，会重新构建并打开独立 Host + 两个可操作 Client；不要加 `-Auto`，即可自行验证行为。

在仓库根目录双击 `Start-Godot-Sync-Lab.cmd`，或：

```powershell
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Relay
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -LiteNetLib
```

脚本构建/导入项目，启动独立 Host 和两个 Godot Client 并自动连接。两个 Client 都是独立进程；Host 只作为权威模拟，不创建隐含本地 Client。

控制：

- WASD：移动。
- SPACE / ATTACK：攻击另一玩家，需在 180 单位以内。
- E / PICKUP：拾取最近的绿色物品，需在 65 单位以内。
- R / RESPAWN：恢复自己的生命值和位置。
- 右侧可设置延迟、UDP 丢包率、乱序，或故意丢掉下一条事件集合增量。
- DISCONNECT / CONNECT：断开和重新入场，Host 释放旧连接拥有的玩家槽位。
- 窗口展示生命值、分数、背包条目、Host tick、位置 revision 和故障注入计数。显示位置经过平滑插值，权威组件仍保存收到的实际状态。

可以单独打开 `Godot/project.godot` 编辑或运行。默认 Direct 端口 28770、Relay 端口 28771；TCP Direct/Relay 使用 TcpTransport.ConnectAsync。`-LiteNetLib` 选择独立 UDP Direct transport，Host 和 Godot Client 必须都使用它；LiteNetLib + Relay 明确拒绝。

关闭 Client 窗口，Host/Relay 控制台用 Ctrl+C 停止。`-Godot <absolute-executable>` 可替换本机路径；已构建时加 `-SkipBuild`。

## 自动验收

```powershell
# 可见窗口 + 自动断言 + 截图
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Relay

# 同样是真实 Godot 进程，使用 headless renderer
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Headless
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -Headless -LiteNetLib
powershell -NoProfile -File Samples/NetRpcGodot/Start-Lab.ps1 -Auto -LiteNetLib
```

每轮创建一个独立 Host、两个实际 Godot Client；Relay 模式还创建独立 Relay。自动脚本执行：

1. 两个连接申请各自的槽位，Host 将真实 sender peer 绑定到槽位。
2. Client 本地直接改 Health 被拒绝。
3. Client 1 移动，Client 2 观察其移动。
4. 25% UDP 丢包、60ms 延迟、140ms 可变延迟造成乱序；组件 revision 不回退。
5. Client 1 拾取，验证 AvailablePickups 列表及 Inventory 字典。
6. 丢掉一条 Events 增量，后续版本断档请求快照，最终收到 HIT 事件。
7. 实际普通类 woven CombatCommands.Attack RPC 在 Host 执行，两个 Client 都得到玩家 2 的 80 HP、Inventory[101]=1、Scores[1]=10。
8. Client 2 冒用玩家 1 的攻击被 Host 拒绝。
9. Client 1 断开并重新连接；Client 2 观察离开/重新加入，新连接收到已有世界状态。

输出在 `Artifacts/NetRpcGodot/<timestamp>-direct|relay|litenetlib-direct/`：`summary.json`（含 Backend）、两份 Client JSON、各进程日志；可见模式还保存两个实际 viewport PNG。自动轮次结束后清理自己启动的所有进程。

## 程序集与边界

| 目录 | 职责 |
| --- | --- |
| Contracts | netstandard2.1 的接口/位置 DTO；没有引擎类型 |
| Game | net8.0 纯 C# 世界、权限/范围/输入约束、两个预先存在 Entity、Component；真实 Cecil 编织的 CombatCommands |
| Session | 构建期接口代理、DI、可选 Native TCP/UDP 或 LiteNetLib Direct 接线、故障注入、自动断言 |
| Host | 独立 .NET 10 权威进程，50Hz 固定步模拟、20Hz 变更快照、1s 全量恢复 |
| Relay | 独立原生 RelayEndpoint |
| Godot | Godot 4.6.1 .NET 的输入/绘制/UI；主线程队列与断言；不使用 Godot MultiplayerAPI 或 Godot RPC |
| E2E | 跨进程监督、结果验证、证据输出 |

编织始终处理纯 Game 程序集，生成接口位于 Session；Godot Node 不参与网络业务体。Client 没有执行权威计算，只读本地同步组件/属性。

Core 的 PeerDisconnected 事件用于 Direct/Relay 通用清理；代理发现支持 Godot 从 bytes 加载程序集导致 Assembly.Location 为空的情况。游戏范围目前固定两个 Entity，符合手写 v2 不自动远端 Spawn/Despawn 的边界。

## 已验证

Godot 4.6.1 `.NET`、Windows、实际 TCP/UDP loopback、Direct/Relay 双 Godot 进程、可见窗口截图、权限拒绝与重连。完整 library suite：251 通过 / 0 失败 / 2 项既有可选性能测试跳过。详细证据见 [Godot 验收](../../Docs/godot-sync-lab-validation.md)。

公网/NAT 路由器、Linux、Godot 导出包与 Unity Player/IL2CPP 不属于本轮运行证据。
