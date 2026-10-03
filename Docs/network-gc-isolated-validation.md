# 隔离网络 GC 优化与健壮性记录

后续状态：本阶段差异现已连同集合功能合入主库 master 工作树；下文是隔离阶段的原始记录。当前 Unity 读回见 [接入记录](unity-sync-collections.md)。

日期：2026-09-28。源码与构建输出均在 `Net.BITKit.Multiplayer.GC`，基于 tree `24ef3bfa7032d13bba98212e83d9a9b4bbb2cddc`（`refs/perf-baselines/gc-isolated`）。**尚未合入 Unity 使用的 live 库。** 没有创建 commit、修改 Unity manifest 或进行本轮 Unity 验证。

## 问题与修改

### UDP 鉴权的每包对象和复制

原实现每包创建 HMACSHA256、将凭据复制为数组再转 Base64 查表、复制待验签数据和 nonce、分配摘要，以及为 UTF-8 claim 创建临时数组。

`Src/TouchSocket/UdpLane.cs` 现在：

- 16 字节凭据使用 `Guid` 值键，仅作为原始 ID 的查表表示；线上凭据字节不变。
- 每 grant 一个 HMACSHA256，签名、验签和撤销均在 lane registry 锁内；I/O 与应用回调仍在锁外。
- `TryComputeHash` 直接读帧 Span，发送摘要直接写 wire，接收摘要写 stackalloc；比较使用 `FixedTimeEquals`。
- nonce 显式小端读写，匹配原 Windows 协议；UTF-8 直接读写 Span。非空接收 claim 仍需一个字符串，没有全局 intern 或无界缓存。
- 替换、移除、过期及 Dispose 统一释放 MAC。nonce 到达 `ulong.MaxValue` 后撤销凭据，拒绝回绕，需可靠控制面重新授予凭据。
- 已签名的在途 frame 独立拥有缓冲；撤销 MAC 不提前归还该 frame。

### Native UDP 同步完成仍创建 operation/TCS

`Src/Transport/UdpTransport.cs` 现在：

- 每个池化 SAEA 搭配一个复用的 SendOperation；同步成功返回完成的 ValueTask。
- 在提交 OS I/O **之前**准备备用 TCS；同步发送保留未发布的备用 TCS，真正 pending 的发送才发布它。已发布的 Task/TCS 永不重置或复用；下一次需要时再补充。
- 同一 gate 覆盖提交与 promise 发布，阻止完成回调抢先归还/复用 operation。锁不跨越 I/O 等待。
- 取消或停止仍等待底层操作结束后释放借用内存；Completion 在已接受发送结算后完成。
- 接收使用私有 wildcard endpoint 模板，避免每次重新 post 时新建模板；框架提供的来源 endpoint 未做可变对象池化。
- 修复自定义 MemoryManager 在读取 Span 时抛异常会遗漏 rental/发送槽清理的问题。

### 测试端口选择

首次 Relay 回归 19/20 通过，另一个在 UDP Bind 阶段返回 AccessDenied。旧辅助函数只证明 TCP 端口空闲。Datagram/Relay 测试现同时试绑 TCP 与 UDP，并有限重试不可用候选端口；它仍存在释放预留到实际启动之间的竞争窗口。最终 Relay 20/20 通过。

## 真实 socket 前后对照

工具：`Tests/DatagramTests/NetworkAllocationTests.cs`，默认跳过，显式设置输出路径才执行。

- Windows `10.0.22631`，实际测试进程 **.NET 8.0.14**，Release。
- 固定 **256 B 应用负载**，不是完整 woven RPC 或 Arena 工作负载。
- 每路径预热 512 次，然后 3 批 × 3,000 次投递；逐次等待接收、核对 sender/claim/负载并核对总数。丢包超时直接失败，不能用减少工作量换低分配。
- `GC.GetTotalAllocatedBytes(true)` 测整个进程，包括发送端、接收端、Relay、计时器、框架与同一测试 harness。无强制 GC，非 EventPipe 采样估算，也非当前线程计数。
- 报表写入发生在测量之外。原始文件记录每批分配、墙钟与 GC collection counts。

表中为三批的**中位数，B/成功投递的应用负载**：

| 路径 | 基线 | 仅鉴权优化 | 最终 | 相对基线 |
| --- | ---: | ---: | ---: | ---: |
| Native UDP，一跳 | 240.00 | 240.00 | 72.00 | -70.0% |
| 鉴权 UDP，空 claim | 1648.01 | 240.00 | 72.00 | -95.6% |
| 鉴权 UDP，`peer-目标` claim | 1776.44 | 280.00 | 112.00 | -93.7% |
| Relay UDP，两跳 | 3400.08 | 512.18 | 176.02 | -94.8% |
| Relay 可靠 memory，两跳 | 1451.56 | 1448.38 | 1449.67 | 基本不变 |

原始证据均在本 worktree 的 `Artifacts/NetworkPerformance/`：

- `baseline.json`：生产代码修改前。
- `auth-only.json`：只修改 UdpLane 后。
- `optimized-final.json`：最终生产代码；`optimized.json` 是此前中间检查点。

保留离群批次，不隐藏数据：基线 UTF-8 claim 三批为 **1776.39 / 2041.08 / 1776.44 B**；最终 Relay UDP 为 **429.62 / 176.02 / 176.00 B**。最终 Relay 可靠为 **1476.18 / 1449.67 / 1448.02 B**。这些进程级尖峰尚未另取 trace 归因，表中数字不是每包硬上限。

结论：鉴权对象/复制是确定的大头，分阶段对照显示空 claim 的稳态鉴权新增分配已消除；native 同步路径再减少约 168 B/投递。残余真实 socket 分配仍存在。墙钟是串行 loopback 与等待测试，不是饱和吞吐、P99 延迟或 GC 暂停证明，不将分配下降等同这些指标同比改善。

### 复现

在 GC worktree 根目录运行，避免其他测试与之并发：

```powershell
$env:BITKIT_NETWORK_BENCH_OUTPUT = "$PWD\Artifacts\NetworkPerformance\repeat.json"
dotnet test Tests/DatagramTests/BITKit.Multiplayer.DatagramTests.csproj -c Release --filter FullyQualifiedName~NetworkAllocationTests --nologo
$env:BITKIT_NETWORK_BENCH_OUTPUT = $null
```

要重新测原实现，将同一 benchmark harness 应用于单独的 baseline 源码副本；不要回滚正在工作的 live 库。之后对照相同运行时、负载与三批结果。测试端口辅助函数变化只影响冷启动，不在测量窗口内。

## 健壮性验证

最终定向回归 **70/70**：Transport 16、Datagram 22、Relay 20、TypedHot 12；opt-in benchmark 另行通过，常规 Datagram 测试中该项按设计跳过。最后移除测试阻塞 await 警告后，变动的 HMAC 测试再次定向通过。netstandard2.1 Transport/TouchSocket 构建 **0 警告、0 错误**。

新增覆盖：

- 并发签名与验签；512 个签名输出与独立旧格式/HMAC 参考实现逐字节一致，128 个认证接收正确。
- 同步无 I/O seam 中预热后 2,000 次签名发送 **0 B** 当前线程断言；不是整个真实网络零 GC。
- 撤销确实 Dispose 旧 MAC；撤销期间 delayed send 的 frame 保持有效至操作完成；新凭据可绑定，旧凭据拒绝。
- nonce 耗尽不回绕。
- 失败 MemoryManager 的缓冲/槽归还与后续发送恢复。
- 交替来源 endpoint 在后续接收后仍保留正确来源。
- operation 复用后，尚未观察的旧失败/成功结果和取消状态不串线。

既有覆盖继续通过：伪造 MAC、重放、乱序、错误端点/过期/跨房间拒绝；Direct/Relay 重绑与暂停；延迟/故障/关闭下的借用内存；64 个 lane 在途上限；native 并发限制、ICMP 恢复、超大包、回调关闭、停止结算；真实 woven typed 调用与零分配断言。

## 剩余热点与交接

- **可靠 Relay 尚约 1.45 KB/投递**。`RelayProtocol.cs` 的 IncomingFrame、Outgoing/TCS、读写等待，以及 DMTP/TCP 的占比需下一次专门 trace；本轮未凭源码猜测占比，也未改有界 Relay 可靠队列。
- 真正异步 native send 仍使用独立 TCS/Task。压力测试验证其可观察语义，但本次 loopback 不保证强制触发每一种 provider 的 pending 完成竞态。
- native 剩余约 72 B/投递、非空 claim 字符串，以及进程级离群批次仍不是零 GC。
- 本次没有重跑完整 Arena、全量 .NET suite、Unity、IL2CPP、Linux、公网/NAT或长时间高人数负载。
- 将 `Src/TouchSocket/UdpLane.cs`、`Src/Transport/UdpTransport.cs`、相关测试及本文/导航变更作为一个待审集合。比较 `git diff refs/perf-baselines/gc-isolated`；排除 sandbox 管理文件 AGENTS/PERF-WORKTREE，合入时逐文件检查 live 并行变化。
