# LiteNetLib Direct Transport：实现 Agent 交接

> 历史 worktree 交接：2026-10-07 已提取为独立 `Net.BITKit.Multiplayer.LiteNetLib` 仓库，旧工作区完整保留到 `Net.BITKit.Multiplayer.LiteNetLib.LegacyWorktree`。当前布局与入口见 [扩展指南](litenetlib-guide.md) 和 [提取记录](litenetlib-repository-extraction.md)。下方路径/集成指令仅用于解释当时的开发过程。

日期：2026-10-03。这里的库是 **LiteNetLib（网络传输）**，不是 LiteDB（数据库）。用户要求把 LiteNetLib 接入工作与 GC 基线/Runtime 优化隔离，允许主 Agent 协调 SubAgent。

## 目标与分工

交付一个真正可运行的 LiteNetLib **Direct** 字节 Transport，复用当前新 NetRpc 的生成接口、普通类 IL Wrapper、ECS、状态和集合协议，并用两个实际 Godot Client 进程验收。

- 主协调 Agent：GC 基线、共用 Runtime/集合/回复内存优化；随后 Unity 最小适配。
- 本实现 Agent：LiteNetLib 依赖、独立 Transport 程序集、连接/监听/生命周期、对应测试及 Direct 示例接线。
- 审查 Agent：只读核对接口、所有权、版本恢复、验收覆盖。

源码根目录：`D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer`。
建议工作区：同级 `Net.BITKit.Multiplayer.LiteNetLib`，分支 `feature/litenetlib-direct`。

本轮保持当前 `ITransport`、线格式、ID、权限和同步语义。不要重写 RPC/状态 Runtime、换成旧 B6/TouchSocket backend，或使用 Godot 内置 Multiplayer/RPC 代替 NetRpc。

## 0. worktree 前提：先核对基线

当前实测：`git rev-parse --verify HEAD` 失败，master 尚无有效提交，仓库文件均未追踪；已有 GC/Sync worktree 也显示 `0000000`。**这些工作区不是包含当前实现的共同提交基线。**

开工先执行并记录：

```powershell
git status --short
git rev-parse --verify HEAD
git worktree list
```

必须由协调者先确认一份包含现有新 NetRpc、Godot Sample 和本交接的共同代码基线。`git worktree add` 不会自动携带主目录的未追踪文件；不得把空 worktree 当作当前源码，也不得仅根据 branch 名认定代码一致。

本交接不授予 commit/push 权限。无有效提交时，先报告这个真实阻塞，不擅自提交整个目录或覆盖既有 GC/Sync 工作区。协调者确定并建立有效基线后，再执行：

协调者也可以用 `Tools/AgentBaseline/Create-IsolatedSnapshot.ps1` 创建 **orphan worktree + 验证过的未提交源文件快照**：冻结源文件到主目录 Artifacts/AgentBaselines，逐文件 SHA256 检查，并记录 manifest。此方式不创建提交，不能称为共享提交 SHA；实现差异必须相对 manifest/frozen sources 审查，由协调者按明确文件清单整合。当前用户已要求并行执行，可采用这条隔离方案继续工作。

```powershell
# 在主仓库运行；BASE_SHA 必须替换为协调者确认的真实提交。
git worktree add -b feature/litenetlib-direct ../Net.BITKit.Multiplayer.LiteNetLib BASE_SHA
```

创建后核对 worktree 中下列入口与基线一致，并记录 SHA。不要把 sibling worktree 的 Src 路径或生成 DLL 混入本 worktree 构建；所有 ProjectReference 和测试路径应指向自己的源码/Artifacts。

## 1. 最小阅读与源码入口

先读 AGENTS.md，再读以下页面；手写设计为需求依据，旧交接只作历史进度参考：

1. `Docs/design-v1.md`、`Docs/design-v2.md`。
2. `Docs/design-implementation.md`。
3. `Samples/NetRpcGodot/README.md`、`Docs/godot-sync-lab-validation.md`。

定点源码：

| 文件 | 需要核对的内容 |
| --- | --- |
| `Src/Runtime/NetRpcV1.cs` | **NetRpc** ITransport/ITransportLifetime、25 B 帧、AttachPeer/DetachPeer/PeerDisconnected、借用输入和请求回复 |
| `Src/Runtime/NetRpcServices.cs` | AddNetRpc 注入现成字节 transport；Host/Client 互斥，无通用监听器/factory 可直接套用 |
| `Src/Transport/TcpTransport.cs` | 当前可靠/不可靠行为、连接关闭和生命周期，仅作行为参考 |
| `Src/Transport/NetRpcRelay.cs` | 当前 Relay 显式依赖 TcpTransport，本轮不声称 LiteNetLib Relay 已支持 |
| `Samples/NetRpcGodot/Session/ArenaSession.cs` | Host DI、多 peer 接线、Client 连接/关闭、自动断言；当前 Client 字段硬编码 TCP |
| `Samples/NetRpcGodot/Session/ImpairedTransport.cs` | 有界延迟/丢包/乱序，当前 inner 硬编码 TCP，需要接口 seam |
| `Samples/NetRpcGodot/Host/Program.cs` | 当前 TCP listener/accept、逻辑 peer 分配、50Hz Host 模拟 |
| `Samples/NetRpcGodot/E2E/Program.cs` | 独立 Host 与两个实际 Godot 进程、断言/日志/截图、清理 |

注意同名陷阱：本任务实现 `BITKit.Multiplayer.NetRpc.ITransport`，**不是**旧 `BITKit.Multiplayer.ITransport` / ITransportFactory。代码中使用明确 namespace 或 alias。

## 2. 程序集与允许修改范围

优先新增：

- `Src/LiteNetLib/`：独立适配器源码和独立 asmdef。
- `Projects/BITKit.Multiplayer.LiteNetLib.csproj`：固定 LiteNetLib 版本，只在适配器引用该依赖。
- `Tests/LiteNetLibTests/`：独立测试项目。
- `Docs/litenetlib-guide.md`、`Docs/litenetlib-validation.md`：实际使用和证据。

选版本前检查它真实支持的 TFM/API；目标至少 netstandard2.1 / net8.0，测试可用 net10。不要因包名存在就推断其 Unity/AOT 或某项 API 支持。

适配器放在 `Src/Transport/` 会被现有 native Transport csproj glob 自动包含，导致 LiteNetLib 依赖泄漏到 native/Core/Unity；因此使用独立目录/项目。UPM 的 Src 源码必须有程序集隔离；缺 DLL 的新 asmdef 不能让现有 Unity 项目进入 Safe Mode。需要 Unity 安装依赖或 gated asmdef 时单独记录，不能把 .NET 构建成功写成 Unity 成功。

本 Agent 不修改：`Src/Runtime/`、RPC 编织器/代理生成器、`Game/ArenaWorld.cs`、共享 Contracts、Unity Project B、业务 Server、现有包依赖版本、既有 GC/Sync worktree。

需要修改的 sample seam 主要在 Session、Host 启动、Godot 参数与 E2E。先以最小、可审查变更或独立 LiteNetLib composition 项目完成，并在 handoff 列出补丁；不要同时修改游戏规则或共享 Runtime。共享 slnx、current-status、总导航的更新与协调者合并，不抢同一个文件。

## 3. 固定 Transport 契约

当前 API：

```csharp
namespace BITKit.Multiplayer.NetRpc
{
    public interface ITransport
    {
        event Action<ReadOnlyMemory<byte>>? OnReceived;
        ValueTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
        ValueTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    }
    public interface ITransportLifetime { event Action? Closed; }
}
```

映射：

- Send → LiteNetLib **ReliableOrdered**。RPC、回复、RpcMap、集合增量和恢复控制保留可靠顺序。
- SendFast → LiteNetLib **Unreliable**。不可靠通知与 Component 快照没有 RPC 返回，没有可靠回退或应用重试。
- Transport 只发送现有 opaque NetRpc 字节，不另套 RPC/实体/方法协议，不把方法名/CLR 类型交给 LiteNetLib 路由。
- 单条 byte connection 实现 ITransport；Host listener/NetManager 在 composition 层维护多个连接，分别交给 Runtime.AttachPeer。
- `AddNetRpc` 目前只消费注入的连接；没有可直接使用的通用 listener/connect factory。实际新增 API 放在适配器，不能误写“已有 factory 可切换”。

### 内存所有权（必须先实现正确，再量分配）

1. outbound ReadOnlyMemory 的借用到返回 ValueTask 完成。要核对选定版本的 NetPeer.Send 是否在返回前复制/消费数据；若自己排队，必须持有拥有的缓冲直到真正消费结束。
2. OnReceived 内存只借用到同步 callback 返回。不得把 NetPacketReader 或内部数组交给异步任务后立即 Recycle。
3. PollEvents、Recycle 和所有事件必须有一个明确的线程/调度所有者。不能多个线程同时 Poll，不能在 Dispose 后继续交付。
4. 若交给引擎线程或故障注入延迟队列，先获得拥有的 payload；队列要有条数/字节上限，所有取消/异常/关闭路径归还。
5. 所有 ValueTask 恰好消费一次。高频路径优先池化，不能用“到处 ToArray”规避所有权设计。
6. 用真实发送结束、借用缓冲立即覆写/复用、并发调用和断连中的发送测试验证，不只测假 transport。

### 大小、顺序与 MTU

NetRpc 当前上限：25 B 头 + 1 MiB 序列化 payload。核对 LiteNetLib ReliableOrdered 的实际分片/重组限制；不能未经测试声称支持当前最大帧。

Unreliable 上限应根据已协商 MTU和选定版本真实 API计算。现有 TCP+UDP 的 60000 B 不能直接套给 LiteNetLib。超过限制明确拒绝，不静默截断、丢弃、转可靠或自行添加应用分片。

不要靠 LiteNetLib 可靠重传推出副作用 RPC 会自动重试；RPC 超时仍是结果未知，不保证没执行。

## 4. 连接身份与生命周期

- 网络 Host 是唯一权威，包括 Dedicated Host；没有额外隐含 Client Runtime。
- Runtime PeerId 1 保留给 Host；Host 上远程连接 ID 不得为 0/1。Client 把权威连接挂为 peer 1。
- Host 将自己分配的逻辑 peer 绑定到 NetPeer 实例/连接世代；不信任 payload 中自称 sender，不把可能复用的 LiteNetLib peer.Id 当作整个房间的永久身份。
- 重连使用新逻辑 ID，或严密隔离旧连接回调。旧包、旧异步回复、旧 Closed 不得作用于新连接。
- 当前 Runtime 入口会检查 connection 引用，但 Closed 闭包按 peerId Detach。实现必须静默旧订阅/回调；如果发现 Runtime 需要额外 guard，提交给协调者处理，不自行改 Runtime。
- Closed 每实际连接关闭一次；Runtime.PeerDisconnected 供业务释放槽位。连接丢失后 pending 不无限等待。
- Runtime 不拥有并自动 Dispose 外部 Transport；listener/session 明确拥有 NetManager、NetPeer、poll loop、connect timeout和 cancellation。
- 两个同进程房间必须使用独立 runtime/scope/连接路由，不能全局 active runtime。
- sample Direct 从 peer 2 分配、现有 Relay 从 1001 分配；若未来混合多个 endpoint，需要协调无冲突 ID allocator。本轮专注 LiteNetLib Direct。

连接凭据与应用权限分开。保留真实 sender、业务 Join/slot owner 校验，不为了连通性关闭 Host 权限检查。

## 5. Godot 接线要求

新增明确的 backend 参数/工厂 seam，让 Client/Host 选择 TCP 或 LiteNetLib；只改变 composition，不改变 IArena、游戏规则、Component ID、scope、RPC 和集合 wire。

至少解除两个现有硬编码：ArenaClient 的 TcpTransport 连接/Dispose，以及 ImpairedTransport 的 inner。可注入 NetRpc.ITransport、ITransportLifetime 和明确 async lifetime owner；不能丢掉断连、超时或故障注入。

Game 保持纯 C#，Godot Node 仅处理输入/UI；网络线程不直接调用 Godot API。复用 main-thread queue，接收事件中的借用 payload 不跨队列悬空。

E2E 必须明确报告所用 backend，启动独立 Host 与两个实际 Godot 4.6.1 .NET 进程。不能把“两个 .NET Runtime”或旧 TCP 路径成功当成 LiteNetLib Godot 验收。Relay 参数在首版 LiteNetLib 模式明确拒绝或标为未实现，不能静默走 TCP 后报告 LiteNetLib Relay 成功。

## 6. 推荐实施顺序

1. 确认基线 SHA、目录、现有代码与测试；固定包版本和 TFM。
2. 独立程序集实现连接 wrapper/listener，可靠与不可靠实际 loopback。
3. 验证内存、容量、PollEvents、取消/关闭、世代身份，再接 NetRpc。
4. 用现有生成接口/woven 普通类执行返回值、void、错误、补表。
5. 接入 Godot 的 backend seam，执行相同 gameplay/fault/lifecycle 断言。
6. 同负载测量，记录修改清单、命令、结果、未覆盖环境；交给协调者合并。

## 7. 必须通过的验收

- 独立适配器构建 netstandard2.1/net8.0，Core 不依赖 LiteNetLib。
- 实际 LiteNetLib Host 与双 Client：可靠顺序 RPC/回复，Unreliable Component/void，不可靠返回类型拒绝。
- 接收缓冲立即覆写/Recycle 正确；队列发送跨 await 与异常/取消不提前释放。
- 最大可靠帧、超过 MTU 的不可靠帧、无效参数边界明确。
- 主动/被动断连、connect cancellation、发送期间 Dispose、停止 PollEvents、重连及旧连接消息隔离。
- scope 隔离、相同 target/entity/component ID 的两个房间不串。
- Godot 可见及 headless 双进程：移动、Host 权威、woven 攻击、拾取、标量/列表/字典、增量断档、丢包/乱序、槽位释放与重连。
- 保持已有 TCP/Relay 和完整 suite 回归，不能删掉或弱化失败断言。

当前参考检查点：solution 251 通过、0 失败、2 项原有可选性能测量跳过；Godot Direct/Relay 已通过，证据在 Docs/godot-sync-lab-validation.md。这个数字是基线，不是要求新测试总数停在 251。

## 8. 性能对照与协调

GC 基线归主 Agent，正在安排，不假定已存在完整真网络 benchmark。适配器仍需记录真实 socket 下的：

- warmed scalar void、带结果 RPC、Component、标量、集合单项更新的 B/op；
- 每进程 MB/s、GC collection count、吞吐、延迟（至少 p50/p95）；
- Host 与 Client 分开，包大小/频率/人数/可靠性/预热/时长一致；
- poll loop、队列字节/条数、租用与归还、关闭后的余留；
- engine/UI 与网络 workload 分开。

不从同步 sink 的 0 B 推出真实 LiteNetLib 0 B。不提前更改游戏负载“优化数字”。先保留原始报告；等主 Agent 的公共基线工具就绪再接入同一入口。

## 9. 交付格式

给协调者的最终 handoff 必须写明：

1. baseline SHA、worktree/branch、改动文件（标出 sample seam 和共享文件候选）。
2. LiteNetLib 固定版本、目标框架、poll/thread/内存所有权策略。
3. 实际运行命令、Host/Client PID、backend 标识、Godot JSON/PNG/log 路径。
4. 构建/测试数量、性能原始报告、真实缺口。
5. 仍需协调的 Runtime 问题/Relay 解耦，不声称这些自动完成。

未经明确请求不 commit/push；不要覆盖主工作树的并发工作。

## 可直接给实现 Agent 的任务

> 先读本交接和仓库 AGENTS。核对无初始提交/未追踪文件问题，由协调者确认共同代码基线后在独立 worktree 实现 LiteNetLib Direct。只新增独立 Transport 程序集/测试和最小 composition seam，保持 NetRpc.ITransport、wire/权限/同步契约稳定。Send=ReliableOrdered，SendFast=Unreliable，严密处理借用/Recycle/关闭/世代身份。复用纯 Game 与实际双 Godot E2E，保留 TCP/Relay 回归。GC Runtime 优化与 Unity 接入由主 Agent 负责。本轮不报告 LiteNetLib Relay 或 Unity/AOT 已支持。
