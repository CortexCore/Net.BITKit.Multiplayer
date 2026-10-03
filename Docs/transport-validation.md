# Native ITransport 验收与 GC A/B — 2026-09-27

这是原生 UDP 替换阶段的检查点，当时可靠游戏消息仍用 JSON。后续已迁移 binary v3，最新 161 项测试与分配结果见 [可靠二进制验收](reliable-binary-validation.md)。

## 完成内容

用户明确批准从 TouchSocket-only packet I/O 演进到可 DI 替换的 ITransport。新增原生 UDP 实现、生命周期/能力契约和工厂注册；Direct Host/Client、Relay Host/Client、Relay server、重绑路径均接入。可靠 TouchSocket 会话和认证端点绑定仍保留。实现与用法见 [transport-guide.md](transport-guide.md)。

## 构建与测试

完整 Release 构建 **0 警告、0 错误**；核心 netstandard2.1，原生 transport 与 TouchSocket adapter 均构建 netstandard2.1/net8.0。

**154 项通过、0 失败、0 跳过：**Runtime 48、原生 Transport 13、Datagram 19、Relay 16、Lobby 9、Game 30、App 19。

新覆盖包括：真实 Socket 收发、1200/1201/大报文拒绝及随后正常收包、发送失败、idle/回调内关闭、关闭前所有接受的发送任务结算、租还计数恢复、启动取消、可靠 delivery 拒绝、DI 自定义工厂优先、不同房间独立端点、重绑创建/关闭端点、失败重绑保留可靠会话、能力不匹配拒绝，以及 Windows ICMP port-unreachable 后仍可正常收包。旧 MAC、重放、过期、身份隔离及回调锁顺序测试继续通过。

纯原生与 lane 的租还结论来自专项计数测试；E2E 节点报告没有暴露全部池计数，不能把正常进程退出冒充逐个租用的验证。同步接收批次让出执行的路径已实现，但没有用确定性测试强制制造持续同步完成。

## 四组顺序 A/B

所有组使用相同 Release 代码、同样的三 Client bot 负载和约 35 秒 Host/Alice trace，顺序执行。每组仅一次，没有失败重试。四组只切换 packet factory 与 Direct/Relay 路线，不降低模拟频率或取消可靠业务。

产物位于 `Artifacts/ArenaRuns/`：

| 路线 | 实现 | 目录 |
| --- | --- | --- |
| Direct | native | `20260927-143111-c89c8b` |
| Direct | touchsocket | `20260927-143257-ca1896` |
| Relay | native | `20260927-143445-784584` |
| Relay | touchsocket | `20260927-143631-7d0062` |

每个目录保留 `profile-window.json`、`profile-host/alice.json`、`.nettrace`、`.etlx` 和业务报告。节点与 Relay ready 文件的工厂名读回分别为 `native-udp` 和 `touchsocket-udp`。

### 全进程分配下降

进程 MB/s = 同一节点 profile-window 前后 `GcCounter.AllocatedBytes` 差 / 该节点 `ElapsedMilliseconds` 差。报告/GC 采样有刷新间隔，近似对齐但不冒充 trace 的精确边界。MB 均为十进制。

| 路线／节点 | TouchSocket UDP：MB/s | 原生 UDP：MB/s | TouchSocket / 原生 EventPipe 估算分配 |
| --- | ---: | ---: | ---: |
| Direct Host | 2.091 | 2.032 | 74.33 / 71.95 MB |
| **Direct Alice** | **3.419** | **0.879** | **120.74 / 30.61 MB** |
| Relay Host | 2.471 | 2.383 | 86.68 / 83.49 MB |
| **Relay Alice** | **3.573** | **1.027** | **125.93 / 35.60 MB** |

本次客户端全进程分配分别下降约 **74% / 71%**。这是一组实际工作负载的观测，不是所有硬件/人数下的稳定基准。

Legacy Alice 的 `TouchSocket.Sockets.UdpSessionBase.RunReceive` 叶栈：Direct **641 样本 / 93.41 MB 估算**，Relay **633 样本 / 91.45 MB 估算**。两个 native trace 中均无该叶栈。结合原生实现的单个接收 rental 和依赖反编译证据，可以确认已去掉该调用路径的每包 64 KiB 数组分配。

Host 改善较小符合当前流量方向：它主要发送 UDP 位姿，客户端输入/响应仍走可靠 JSON。原生路径仍有 HMAC、端点对象、RPC/JSON、发送 operation/TCS、快照和报告分配，**不是零 GC**。例如 Direct native Alice 的同窗同步阶段中，输入约 5.57 MB、插值 4.65 MB、报告写入 2.61 MB；这些阶段不覆盖异步收包线程，也不能与全进程总量相加。

### GC 次数降低，最长暂停不保证降低

| 路线／节点 | TouchSocket GC 次数 / 最长 | 原生 GC 次数 / 最长 |
| --- | --- | --- |
| Direct Host | 6 / 1.54 ms | 5 / 1.78 ms |
| Direct Alice | 9 / 0.67 ms | 2 / 4.03 ms |
| Relay Host | 7 / 1.71 ms | 6 / 4.20 ms |
| Relay Alice | 10 / 1.14 ms | 3 / 3.17 ms |

这里只计算窗口内完整 `SuspendForGC` / `SuspendForGCPrep` 区间，不含 `SuspendOther`。降低分配不代表每次 GC 的最坏耗时单调下降；短窗口且样本少，不据此宣布帧时间或 P99 改善。

### 业务量核对

- Direct Host UDP：native **9,370**，legacy **9,350**；射击/命中分别 **272/50** 与 **269/51**。
- Relay UDP 转发：native **9,276**，legacy **9,353**；射击/命中分别 **266/46** 与 **269/50**。
- 四组均通过成员、输入、Health、死亡/复活、晚加入、离场及正常关闭检查；Relay 最终 rooms/clients/pending admissions 为 0。

时序会造成 bot 结果小幅差异，因此不是逐帧确定性回放，但没有通过降低业务量制造分配改善的迹象。

## 原生 Relay 图形与通道验证

运行：`--transport native --relay --pause-udp --rebind-udp --visual`。

证据目录：`Artifacts/ArenaRuns/20260927-143818-b76e57`。

- 暂停 UDP：PositionTick 固定 **64**，收到的 UDP 包数固定 **39**，可靠探测 **1 → 5**、Health 更新 **4 → 7**。
- 恢复后 PositionTick **156**，可靠连接未断开。
- 实际重绑保留 Peer/Player/Scope，完成标记附近 Tick **498**，之后 **503**，替换 Socket 已收到 7 个 UDP 包。
- 实际 UDP 最大包 **550 B**，未超过 1200 B。
- 最终 Relay rooms、clients、pending admissions、UDP bound peers 全部为 0。
- `alice.png`/`bob.png` 已读回检查，2D/3D 窗口均显示 Relay connected/ready、玩家/Health 与 UDP-bound 状态。实现选择通过 JSON 的实际工厂名验证，截图本身不证明 Socket 实现类型。

本轮没有 Unity、Linux、公网/NAT、数小时压力或目标设备验收。KCP/RUDP 未实现；ITransport 的可替换边界不等于所有协议已经具有同样的可靠性或握手语义。
