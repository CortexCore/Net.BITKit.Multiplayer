# Arena V4 — MemoryPack、UDP 联动与位置插值 Sample

普通 woven 游戏调用当前走 **typed B6/v4**：构建期生成强类型编解码和直接调用器，void 为单向通知，缓冲通过 `IRoomMemoryWire` 借用到发送完成。状态/Task reply 等仍为 binary v3。见 [当前使用说明](../../../Docs/typed-rpc-guide.md) 与 [182 项测试及最新分配结果](../../../Docs/typed-rpc-validation.md)。

最新可靠游戏 RPC/返回值/SyncVar 也已迁移到 **binary v3 + MemoryPack**，不再走 Runtime JSON/JToken；同房间需统一升级。见 [DTO 迁移指南](../../../Docs/reliable-binary-guide.md) 与 [161 项测试及分配对照](../../../Docs/reliable-binary-validation.md)。

.NET 10 + TouchSocket 4.3.9 + Raylib-cs 8.1.0。纯代码、基础图形，无 Unity/Godot 编辑器或外部美术资源。

当前高频位姿已迁移到真实 Direct/Relay UDP，参数直接使用 MemoryPack。见 [V4 使用说明](../../../Docs/v4-guide.md)、[验证记录](../../../Docs/v4-validation.md) 和 [GC／缓冲回收报告](../../../Docs/v4-gc-report.md)。

UDP 底层当前默认 **原生 Socket + 可 DI 替换的 ITransport**；可靠连接仍为 TouchSocket。见 [Transport 使用](../../../Docs/transport-guide.md) 与 [154 项测试及 GC A/B](../../../Docs/transport-validation.md)。启动器可用 `-Transport native|touchsocket`，App/Relay/E2E 可用 `--transport native|touchsocket`；旧实现只用于显式对照。

## 一键启动（Windows）

最简单：双击仓库根目录的 **`Start-Arena.cmd`**。它会自动构建，然后启动 Lobby、Relay、玩家 Host 和一个 Client；无需手动打开多个终端。合作房间默认 Relay，Dedicated Host 默认 Direct，客户端从房间描述自动选择。
也可双击 **`Start-Arena-Relay.cmd`** 明确强制中继；路由说明和部署准备见 [Relay 指南](../../../Docs/relay-guide.md)。

想比较平滑效果：双击 **`Start-Arena-Latency.cmd`**，客户端位置显示加入 **120ms 延迟和 ±60ms 抖动**，在游戏窗口按 **F3** 开关插值。普通启动默认开启 100ms 插值，不注入额外延迟。

在仓库根目录运行：

```powershell
# Lobby + 带窗口的玩家 Host + 一个 Client（两个窗口）
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1

# Lobby + 无本地玩家的 Dedicated Host + 两个图形 Client
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -DedicatedHost -Clients 2

# 已构建时跳过构建；端口占用时可换端口
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -NoBuild -LobbyPort 17990 -GamePort 17991
```

启动器先统一构建，再运行独立进程。Game 程序集在构建时自动从原始中间产物编织，重复构建不会重复编织已修改的程序集。
不要在运行中的演示进程还锁着 DLL 时重新构建。启动器终端按 Enter 可结束自己启动的进程；不会结束其他游戏或 dotnet 进程。

### 操作

- **WASD**：移动。
- **鼠标**：瞄准；**左键**：开火（仅在场地内点击）。
- **F2**：2D 俯视／3D 切换，不重连。
- **F3**：位置插值开关，方便与 20Hz 直接显示比较。
- **F4**：本节点 gameplay UDP 开关；可靠输入、Health、连接与绑定心跳继续工作。
- **F5**：Client UDP 端点重绑，保持 Peer/Player/Scope 身份。
- **Escape／关闭窗口**：离开房间。
- 绿色为本地玩家，橙色为其他玩家；灰色为死亡等待复活。
- 默认血量 100，命中伤害 25，射速间隔约 0.25 秒，死亡约 3 秒后复活。
- 右侧显示角色、Peer/Player/Room、Tick、玩家列表、HP、开火及命中次数。

玩家 Host 直接使用同一个 Host Runtime 和权威模拟显示画面、处理自己的输入；没有额外的本地 Client Runtime。

## 手动分进程

先构建一次：

```shell
dotnet build Net.BITKit.Multiplayer.slnx -c Release
```

以下每行各在一个终端运行：

```shell
dotnet Artifacts/bin/Arena.Lobby/Release/net10.0/Arena.Lobby.dll --port 17890
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role host --name Host --route direct --lobby-port 17890 --game-port 17891
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role client --name Alice --lobby-port 17890 --view 2d
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role client --name Bob --lobby-port 17890 --view 3d
```

Client 未指定 `--room` 时选择列表中的第一个房间。Host 可用 `--room-name` 区分多个房间；多房间必须配置不同 game port。
上述手动例子显式使用 Direct。Relay 房间则可共用一个 Relay 端口，Host 使用 `--route relay --relay-address <地址> --relay-port <端口>`，并先启动 Arena.Relay；Host 自己不监听 game port。
Dedicated Host 添加 `--headless --no-local-player`。单纯 `--headless` 只是不显示窗口，不代表移除 Host 自己的玩家。

## 登录与匹配

默认 `--auth guest --name Alice` 使用游客。也支持：

```shell
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role client --auth register --username demo_alice --password example-only-password
dotnet Artifacts/bin/Arena.App/Release/net10.0/Arena.App.dll --role client --auth login --username demo_alice --password example-only-password
```

以上密码仅是演示数据。Lobby 使用 salted PBKDF2 保存账号密码摘要，`--data <path>` 可保存账号；会话、房间、票据不持久化。
Lobby 默认只监听 loopback，用途是本地开发样例；没有改动 Project B 已有账号／商城服务器。细节见 [Lobby/README.md](Lobby/README.md)。

连接过程：远程接口登录 → 房间目录 → 短期单次加入票据 → Host 在实际游戏连接上兑换票据 → 分配 PeerId →
Client 完成服务绑定 → 同连接准备信号 → Host 成员目录 → Client 确认目录就绪 → 放行 All 广播。
退出也有同连接 ACK，保证 Host 处理完成员离场后客户端再关闭连接；TCP 关闭仍有兜底清理。

## 同步路径

代码入口：`Game/ArenaSession.cs` 中的 `ArenaGameService` 与 `PlayerHealth`。

```csharp
[Rpc(SendTo.Host)] public void SubmitMove(float x, float z, uint sequence);
[Rpc(SendTo.Host)] public void SubmitShot(float x, float z, uint sequence);
[Rpc(SendTo.Host)] public Task<RoomSnapshot> RequestSnapshot();
[Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)]
public void PublishPlayerMotion(long tick, ArraySegment<PlayerMotionUpdate> poses);
[Rpc(SendTo.All, Delivery = RpcDelivery.Unreliable)]
public void PublishBulletMotion(long tick, ArraySegment<BulletMotionUpdate> poses);
[SyncVar] public int Health { get; private set; }
```

- 输入身份取真实 `RpcCallContext.Sender`，不是客户端自报 PlayerId。
- Host 以 20 Hz 固定步推进位置、子弹、线段碰撞、伤害与复活，校验输入序号、有限值和开火间隔。
- 持续位置／朝向通过 **Unreliable RPC → MemoryPack → UDP** 广播，按实际编码大小分批；名称和身份不重复塞入每帧位姿。玩家/子弹生命周期、HP 与初始完整快照仍可靠。显示插值保留，客户端预测尚未实现。
- HP 来自每个玩家的真实织入 SyncVar 对象。RoomSnapshot 帮助发现成员和当前场景，不能替代 HP 的状态同步。
- 晚加入先请求带 Tick 的当前快照，随后继续接收更新。生命周期依赖成员目录，而不是根据某一帧稀疏位置包删除玩家。
- Unreliable 顶层数组/片段最多 256 项，应用帧最多 900 B、完整 UDP 包最多 1200 B；只编码片段有效区间。嵌套引用/集合不在此版本支持范围；**不等于支持 SyncVar List/Dictionary 内部变化**。
- 图形只读取分离的快照，Raylib 全部在窗口线程调用；网络接收不调用绘图 API。

## V2.1：只平滑显示，不改变权威状态

`App/ArenaPresentation.cs` 不依赖 Raylib。控制器保留原始 `View` 给输入、bot 和权威报告，另外生成 `RenderView` 绘制。
玩家和子弹共用一个 20Hz Tick 的连续显示时钟，默认约 100ms 缓冲；历史/等待队列各最多 32 份。没有可用的新状态时停止在最新样本，不无限外推。

- Host 自己控制的玩家直接显示当前权威位置；Client 自己和其他玩家暂时都使用同一种显示插值，没有本地预测。
- 出生、Peer/Scope 变化、死亡/复活和大位移直接重置相应历史。已删除对象不能从旧的排队快照复活。
- 血量、死亡次数、成员资格立即反映权威值；插值只改变位置/瞄准朝向，不延迟伤害判定。
- 长时间无快照或暂停窗口后恢复，会前移到可用缓冲区域，不花几秒重播过期位置。
- **POSE DELAY/JITTER 是显示快照链路模拟，不是全网络延迟或 RTT**；登录、输入 RPC 和 Health SyncVar 不受这两个模拟参数影响。Host 不注入这层人工延迟。
- F3 关闭插值后仍消费相同的延迟/抖动快照，保证比较不是把模拟也一起关闭。

```powershell
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -PoseDelayMs 120 -PoseJitterMs 60
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -NoInterpolation
```

App CLI：`--interpolation-ms 100`、`--no-interpolation`、`--pose-delay-ms 120`、`--pose-jitter-ms 60`。
HUD 显示 INTERP/DIRECT、缓冲长度、RenderTick/样本数和显示模拟参数。JSON 的 `Final` 始终是权威状态；`Presentation.Frame` 是显示值。

## 自动化：独立 OS 进程验收

```shell
# 不创建图形窗口，适合自动化环境
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj

# 创建两个真实 Raylib 窗口，并保存 2D/3D framebuffer 截图
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --visual

# Alice/Charlie 开启插值、Bob 关闭；均使用相同的显示延迟/抖动
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --visual --compare-interpolation --pose-delay-ms 120 --pose-jitter-ms 60

# 真实 Relay：暂停 UDP 验证可靠 Health 继续，再恢复并重绑端点
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --pause-udp --rebind-udp --visual

# 延长无窗口运行，记录整个进程的 GC 与分配窗口
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --soak-seconds 90

# 外部 EventPipe 采集真实 Host/Client 分配堆栈、GC 暂停，并启用阶段计数
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile
dotnet run -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay
```

E2E 会启动独立 Lobby、Dedicated Host、Alice、Bob，以及晚加入的 Charlie；Relay 模式另有独立 Relay，默认约一分钟内自动清理，soak 模式延长运行。
检查注册／登录／错误密码／游客、成员一致性、射击伤害、死亡复活、晚加入、第三个玩家移动、离场和房间关闭。
每个进程独立产生 JSON 状态报告，不用单进程共享对象冒充网络通信。

产物位于 `Artifacts/ArenaRuns/<timestamp-id>/`：
- `summary.json`：断言结果和进程 ID。
- `host.json`、`alice.json`、`bob.json`、`charlie.json`：各节点观测的最终状态与指标。
- `Gc`：ready 后预热 3 秒，每秒采样；只保留起始、采样堆峰值和末次快照，不积累无限历史。包含分配量/速率、各代 GC 次数、堆及工作集。正常运行不强制 GC。
- `--allocation-phases` 启用固定数量的同步阶段分配/耗时计数，保存在节点报告的 `Allocations`；`--profile` 自动开启并另存 `profile-window.json`、原始 `.nettrace` 和解析后的堆栈/暂停 JSON。详见 [性能归因报告](../../../Docs/performance-gc-attribution.md)。阶段计时不是 CPU 时间，也不包含其他线程上的收包工作。
- `*.log`：进程日志，不写口令、会话令牌或加入票据。
- `alice.png` / `bob.png`：启用 `--visual` 时的 2D / 3D 截图。

App 另支持 `--udp-off-after <seconds>`、`--udp-on-after <seconds>`、`--udp-rebind-after <seconds>`、`--duration <seconds>`、`--bot idle|move|shoot`、`--report <path>`、`--ready-file <path>`、
`--capture <png>`、`--window-x` / `--window-y`，用于自行编排测试。

## 当前验收与范围

以下 V2/V2.1/Relay 数量为历史检查点；最新整合、真实 UDP 通道隔离、回收上限和 GC 实测统一见 [V4 验证记录](../../../Docs/v4-validation.md)。

2026-09-27 Windows x64 实测：Release 构建成功；V2 原有核心 36、Lobby 3、Game 16 共 55 项回归通过，V2.1 新增显示测试 **17 项通过**，合计 **72 项**。
真实多进程 visual E2E 通过，2D/3D PNG 已人工读回检查，报告中无未处理错误，Host 最终无残留玩家/子弹。
具体记录见 [完整验证记录](../../../Docs/arena-v2-validation.md)。
插值验收见 [V2.1 记录](../../../Docs/arena-v2.1-validation.md)。
后续 Relay 阶段：完整 Release 测试已扩展为 **98 项通过**，六进程中继图形联动、强制关闭 Relay/Host、Direct 回归均通过；见 [Relay 验收记录](../../../Docs/relay-validation.md)。

本次验证了本机真实 TCP/DMTP 与图形显示；没有宣称公网部署、跨平台图形、Unity/IL2CPP 或人工逐键操作已经验收。
