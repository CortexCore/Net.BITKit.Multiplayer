# NetRpc 热点优化：隔离 worktree 与前后实测

2026-10-03。依据 [原始调用栈报告](netrpc-hotspots.md)，优化新 `BITKit.Multiplayer.NetRpc` 的实际 TCP+UDP 路径。

**后续已整合主源码工作树**，保留并行 Unity 接入并再次验证编译/真实 sockets/主线程/退出。见 [主目录整合证据](netrpc-main-integration.md)。下文是独立 worktree 阶段记录，原始性能附件仍在该 worktree 的 Artifacts，未通过本文复制进主目录。

本页是第一轮 Task/ValueTask 优化的历史检查点。随后已按用户要求完成 [默认 UniTask 端到端链路](netrpc-unitask-default.md)，scalar 再从 352.11 降到约 96.14 B/op；当前 API 和 UniTask 依赖以新页为准。

## 工作区与基线

- Worktree：`D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.Hotspots`。
- 分支：`perf/netrpc-hotspots`。
- 原仓库没有 HEAD，文件均未跟踪；使用 `git worktree add --orphan -b perf/netrpc-hotspots ...`，复制开工时的源码、测试、工具和文档。没有创建提交。
- 本轮未改动原目录 `Net.BITKit.Multiplayer` 的源码。复制排除 `.git`、`.idea`、`Artifacts`、`bin`、`obj`、`.godot` 和个人 solution 设置。
- 开工源码另存于本 worktree 的 `Artifacts/HotspotsBaseline/`；无 HEAD 时审核修改应与此目录做 `git diff --no-index`，不能依赖普通 `git diff`。
- 本轮是独立 .NET 库工作：遵循多人库 AGENTS 和新设计文档；验证路径为 .NET build/test、实际生成/编织程序集、独立 TCP/UDP/Relay 进程。没有修改 Project B Unity 项目或切换它的包引用。
- 交接时核对主目录发现并行 Unity 接入修改（Contracts/ILPP、NetRpcGeneration、NetRpcProbe、Unity/NetRpc Dispatcher/Probes）。这些不在开工快照中；后续合入仅逐文件迁移本轮差异，不应整目录覆盖主库。

## 实现

### RPC 等待、取消与结果

`NetRpcRequests.cs` 提供每个 Runtime 自有的类型化 `ManualResetValueTaskSourceCore<T>` 等待器池。最多 4096 个 pending，每个结果类型缓存最多 32 个已消费等待器；回收只能发生在单次 `GetResult` 后，取消登记在锁外 Dispose。

- 普通请求不再创建 timeout CTS、linked CTS、取消闭包、`Task<NetRpcModel>` 或三层客户端 async 状态机。
- Runtime 共用一个计时器，仅有 pending 时启用，约 10ms 扫描超时；调度延迟仍由平台决定。只在传入可取消 token 时登记回调。
- 类型化回复在同步接收回调内解码，不再复制整段回复 payload；显式 `SendRequest(model, Type)` 兼容路径仍拥有回复副本。
- `RpcContext.Request/Request<T>` 保留 Task API，只在边界 `.AsTask()`；新增 `RequestValue/RequestValue<T>` 直接返回 ValueTask，必须消费一次。
- 生成 Task/ValueTask 代理和 woven FinishTask/FinishValueTask 直接返回底层结果，不再用额外 await 保留 writer。SendFrame 在返回前冻结参数，实际发送完成后才归还 frame。
- 生成 UniTask 接口代理直接 await `RequestValue`，无需中转客户端 Task。普通类 woven UniTask 的既有 Task→UniTask 适配仍保留；Core 不引入 Cysharp 依赖。
- Host `Execute` 改用 ValueTask，同步完成不创建 `Task<NetRpcModel>`。真正异步业务、上下文、远端异常仍保留。
- 接收入口改成同步分流，在参数/结果解码完成后观察未完成 Task，避免每个包进入 async-void builder。
- 断连/Dispose 结束等待；取消/超时结束等待但不授权提前归还仍在发送的 buffer，也不表示撤销已开始业务。迟到回复和迟到发送失败按 request ID、target、method、连接实例隔离。

### 状态发布与接收

- Peer、StateMember、ComponentEntry 使用生命周期变更时发布的 dense 数组快照；稳定 tick 不再取得 `ConcurrentDictionary.Keys/Values` 复制视图或字典枚举器。
- 扇出携带快照中的连接实例，跳过已 detach 或同 PeerId 的替换连接；Closed 回调也绑定原连接世代。
- 标量属性注册时构建类型化 getter/writer，tick 不用反射读取并 boxing int。
- 服务器 SyncRequest 筛选移入独立方法，普通 Client 增量入口不再创建捕获 model 的 LINQ closure。
- 状态与组件接收租用/归还已有 reader，异常/旧版本早退也归还；状态恢复响应绑定请求来源连接。

### TCP 与 UDP

- TCP 完整读取循环内联到长寿命 ReceiveLoop，保留半包、EOF、0 长度帧和帧上限，不再每个 frame 创建两层 ReadExactly Task。
- UDP 每连接复用一个池化 65536 B 接收缓冲，同步 Deliver 返回后才复用，Dispose 等接收操作结束后归还。
- net8.0 使用 `Socket.ReceiveFromAsync(Memory, SocketFlags, SocketAddress, token)`；稳定端点复用 SocketAddress/endpoint，只有地址改变时才创建 endpoint。发送用 Socket 的 Memory/ValueTask 路径。
- netstandard2.1 使用复用 SocketAsyncEventArgs + ValueTask source 的接收器，不再由 UdpClient 每包返回新数组/Task；底层 endpoint 对象和兼容发送路径仍可能分配。
- UDP token、地址检查、proof/ACK、NAT 端口学习、60000 B payload 上限和不可靠通道语义保留。

## 三轮未插桩前后对照

Windows x64、.NET 10.0.11、native Direct、独立 Host/Client、并发 1。沿用原报告 workload：Scalar 预热 3000 / 测量 30000，Component 与 256 项 Dictionary 预热 2000 / 测量 20000。工具的业务/最终值/回调断言全通过。

口径为 Host+Client 全线程精确分配总数除以完成次数；无 idle 扣除，预热和 JSON 输出在窗口外。每侧三次，下面取总数的中位数，不拿原报告带 trace 的数字作未插桩基线。

| 负载 | 优化前 B/op | 优化后 B/op | 分配下降 |
| --- | ---: | ---: | ---: |
| Task<int> scalar RPC | 2262.92 | 352.11 | **84.44%** |
| ECS int Component | 921.77 | 0.146 | **99.984%** |
| 256 项 Dictionary Set | 406.88 | 0.114 | **99.972%** |

Component 与 Dictionary 每一轮均收到 **20000 次应用回调**，最终值/版本正确。接近零的值仍含固定控制/排空和调度开销，不宣称整体零 GC。Scalar Client p95 中位数为 0.1323→0.1205ms，只是本机 loopback 延迟观察，不是 CPU 占用结论。

三轮合计精确字节数（按运行顺序）：

| 负载 | before-1 / 2 / 3 | final-1 / 2 / 3 |
| --- | --- | --- |
| Scalar | 67887640 / 67887904 / 67855584 | 10560104 / 10563344 / 10563304 |
| Component | 18435424 / 18503832 / 18194496 | 1784 / 6656 / 2920 |
| Dictionary | 8137592 / 9027392 / 7929792 | 2280 / 5120 / 2280 |

证据：`Artifacts/NetRpcPerformance/before-{1,2,3}-{scalar,component,dictionary}/` 与 `final-{1,2,3}-{scalar,component,dictionary}/`，各含 Host/Client、aggregate、构建/编织证明和 suite JSON。`initial-after-*`、`after-*` 是优化过程中间点，不用于最终表格。

## 最终 EventPipe 读回

`Artifacts/NetRpcPerformance/final-trace-scalar/` 包含两份原始 nettrace、Host/Client hotspots JSON 和精确计数。两侧 marker window 均成立，eventsLost=0、truncated=false。

- Client：31 个有效分配样本，权重 3.29 MB，精确分配 3.36 MB。抽样类型只剩 `ValueTaskSourceAsTask<int>` 与异步 continuation 的 ThreadPool work item；原 Dispatch/Request/代理状态机、取消 CTS/Registrations/Timer 和回复数组不再出现于此窗口的分配类型。
- Host：68 个有效样本，权重 7.22 MB，精确分配 7.20 MB。剩余为业务 `Task<int>`、接收器 `Task<NetMessageBag>` 和 ExecutionContext/AsyncLocal/NetRpcCallContext；原 ReadExactly 状态机和 `Task<NetRpcModel>` 不再出现。
- AllocationTick 是抽样，未采到不等于绝对不存在。最终精确总量取 GC 计数，inclusive 栈不相加。CPU 等待栈仍不解释为占用百分比。

## 行为与构建验证

完整 solution Release 构建通过；最终 Runtime 变更后再次完成 Core/生成器/Transport/工具/Sample 编译与以下受影响回归。

| 回归项目 | 通过 | 原有跳过 |
| --- | ---: | ---: |
| Core（含 NetRpc 定向 37 项，新增热点回归 8 项） | 147 | 1 |
| TransportTests | 16 | 0 |
| DatagramTests | 22 | 1 |
| RelayTests | 20 | 0 |
| LiteNetLibTests | 15 | 0 |
| Arena App / Game / Lobby | 19 / 30 / 9 | 0 |
| NetRpcPerformance guards | 12 | 0 |
| **按项目最新结果合计** | **290** | **2** |

两个旧源码形状断言要求代理必须有 async 状态机，已更新为直接调用 Request 的实际源码/IL 断言，没有取消网络行为 guards。首次整套运行中的该 guard 失败记录保留；最终 Core 与 guards 的复跑 TRX 分别为 `core-final.trx`、`performance-guards-final.trx`。

新增热点回归覆盖同步 ValueTask 1000 次 **0 B**、预取消不发送/token 保留、超时与取消期间的异步发送 buffer 生命周期、旧回复/旧发送错误与复用等待器隔离、128 并发混合 Task/ValueTask/UniTask、广播中途连接替换、旧 Closed 回调、TCP 半包/空帧/连续帧。已有回归继续覆盖远端异常、Dispose、scope、UDP 映射、最大帧、借用内存立即覆写、状态恢复和版本原子性。

额外 `Tests/NetRpcCompatTests.csproj` **4/4**：强制加载 netstandard2.1 Transport（TargetFrameworkAttribute 断言），运行真实 TCP+UDP、UDP 端口重映射、最大 TCP 帧三项行为回归。运行宿主仍是 .NET 10，不是 Unity Mono。

最终 Sample 的 Direct / Relay 均 PASS；真实进程矩阵 **26/26**（两个 Transport、九类 workload、集合 1/128/256 项）；Scalar **64 并发** Direct/Relay 各 10000 次完成，结果和帧计数全部通过。矩阵证据在 `final-regression-matrix/`，并发证据在 `final-concurrency-64/`；测试 TRX 在 `Artifacts/TestResults/`。

## 重现与下一步

在此 worktree 执行：

```powershell
dotnet build Tools/NetRpcPerformance/NetRpcPerformance.csproj -c Release -m:1
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile scalar --warmup 3000 --iterations 30000 --timeout-seconds 25 --output Artifacts/NetRpcPerformance/recheck-scalar
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile component --warmup 2000 --iterations 20000 --timeout-seconds 25 --output Artifacts/NetRpcPerformance/recheck-component
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile dictionary --sizes 256 --warmup 2000 --iterations 20000 --timeout-seconds 25 --output Artifacts/NetRpcPerformance/recheck-dictionary
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --filter FullyQualifiedName~NetRpc
dotnet test Tests/NetRpcCompatTests.csproj -c Release --filter "FullyQualifiedName~RealTcpAndUdp|FullyQualifiedName~AuthenticatedUdp|FullyQualifiedName~TcpFrameLimit|FullyQualifiedName~CompatibilityRun"
```

下一轮先读本页、最终 trace 与 `NetRpcRequests.cs`。若业务允许修改契约，ValueTask/UniTask 接口可消除客户端 Task 边界；仍应使用真实相同负载对比。Host typed receiver 的 `Task<NetMessageBag>`、上下文及高并发 socket/发送状态机是后续热点，不能把本轮并发 1 的 B/op 外推到任意并发。

本轮未做 Unity Editor/Mono、Player/IL2CPP、Godot 可见窗口、公网/NAT 路由器和长时间压力验证；源码仍留在隔离 worktree，尚未合入主目录或发布。合入前与 `Artifacts/HotspotsBaseline` 对照，并保留主目录的并行修改。
