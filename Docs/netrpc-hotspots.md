# 新 NetRpc：实际分配栈与 CPU 采样热点

2026-10-03。本次只增加 profiling 工具/测量标记，没有按猜测改 Runtime。

后续已按本报告在 `Net.BITKit.Multiplayer.Hotspots` 独立 worktree 优化并做三轮未插桩对照与最终 trace；见 [热点优化证据](netrpc-hotspots-optimization.md)。下文保留优化前的原始测量，不代表当前 worktree 的 Runtime。

## 方法与证据

对实际独立 Host/Client 的 native TCP+UDP workload 使用 EventPipe：CLR allocation ticks + JIT/loader/rundown + SampleProfiler。每个进程都有 MeasurementStart/Stop 标记，分析窗口排除启动、预热和最终 JSON 输出。

新增 `Tools/NetRpcTrace`（TraceEvent 3.1.21）：分析 `.nettrace` 的分配类型/完整调用栈/方法归因和 CPU samples；捕获通过 `Tools/NetRpcPerformance --trace true` 自动完成，不需要另外安装全局 dotnet-trace。

| workload | 预热/测量次数 | Host 全线程精确分配 B | Client B | 合计 B/op |
| --- | --- | ---: | ---: | ---: |
| Task<int> scalar RPC | 3000 / 30000 | 18512216 | 49332584 | 2261.49 |
| ECS int Component | 2000 / 20000 | 7041384 | 11334184 | 918.78 |
| 256 项 Dictionary Set | 2000 / 20000 | 1760488 | 5877184 | 381.88 |

三组行为/最终值断言均通过，Component 与 Dictionary 各收到 20000 次应用回调。这些数值包含 trace overhead 和少量控制/排空，不代替未插桩基线。

证据目录：

- `Artifacts/NetRpcPerformance/hotspots-scalar/`
- `Artifacts/NetRpcPerformance/hotspots-component/`
- `Artifacts/NetRpcPerformance/hotspots-dictionary/`

各目录包含两份原始 nettrace、host/client-hotspots.json、原始 GC 计数/时间/进程报告、aggregate。AllocationTick 是抽样归因，其加权类型百分比不是精确类型字节数；inclusive 栈会重复覆盖同一分配，不得相加。

## 结论 1：带结果 RPC 的大头是 async/等待器，不是参数大数组

Scalar Client 有 465 个有效分配样本，权重 49.54 MB，接近精确总数49.33 MB。

按抽样类型：

- `DispatchAsync` 状态机约 17.7%。
- `RpcContext.Request<int>` 状态机约 12.9%。
- 生成代理 Scalar 状态机约 3.7%。
- Cancellation `Registrations` 11.6%、`CallbackNode` 10.1%、`TimerQueueTimer` 6.7%、`Linked1CancellationTokenSource` 3.4%。这四项合计约31.8%，实际栈进入 `DispatchAsync` 的超时/链接取消注册。
- 等待结果 `Task<NetRpcModel>` 约7.5%。另有 pending/continuation/closure 等类型。

源码：`NetRpcV1.cs DispatchAsync` 每次创建 Pending/TCS、超时 CTS、linked CTS 和 callback；代理 → Request → DispatchAsync 三层 async 各自保留状态机。结果 payload ToArray 仍存在，但它不是这次小整数 RPC 的最大抽样类型。

Host scalar 分配样本权重18.50 MB。TCP `ReadExactly(byte[])` 与 `ReadExactly(Memory<byte>)` 两层状态机类型合计约48.3%；其他主要是 Task<int>、Task<NetMessageBag>、Task<NetRpcModel>，以及 NetRpcCallContext/ExecutionContext/AsyncLocal map。

下一优先级：减少冗余 await/state-machine 层、简化超时/取消登记及请求等待器；必须保留异步返回、异常、断连/取消/超时和 buffer 生命周期。

## 结论 2：组件发布中有隐藏的集合快照和 boxing

Component Host 权重7.01 MB：

- ConcurrentDictionary `_state.Values` 产生 StateMember[] / ReadOnlyCollection / array Enumerator。
- `_connections.Keys` 产生 UInt32[] / ReadOnlyCollection / array Enumerator。
- `_components` 枚举器与反射读标量产生的 Int32 boxing。
- Udp SendAsync 的 Task<int>。

源码：`PublishStateCore` 遍历 `.Values`，`PublishComponents/SendMember` 遍历 `.Keys`。ConcurrentDictionary 的 Keys/Values 是复制的集合视图，不是免费活视图。即便 Component 本身用了 bag/ArrayPool，每次 tick 仍有管理对象分配和锁成本。

下一优先级：在生命周期变更时发布可复用的目标/peer dense snapshot，或采用不复制 Keys/Values 的明确遍历策略；保持并发 detach、scope、连接世代检查。

## 结论 3：UDP 接收的字节数组和端点包装确实是热点

Component Client 权重11.36 MB：Byte[] 约29%；UdpClient.ReceiveAsync 的状态机约14%；SocketReceiveFrom 包装 Task、IPAddress、IPEndPoint、SocketAddress 占明显份额。

实际调用栈来自 `TcpTransport.ReceiveUdp -> UdpClient.ReceiveAsync`。这部分为真实接收分配，不是 Godot UI/故障注入的 ToArray。本次没有使用 Godot，也没有延迟/丢包 wrapper。

下一优先级：原生 UDP 可复用 receive buffer/endpoint 的 socket 路径；或把 LiteNetLib 用同一 workload 测量后做取舍。不能仅凭此数据认定 LiteNetLib 已更快。

## 结论 4：字典整容器复制修掉后，热点迁到派发和 Transport

Dictionary Client 权重5.94 MB：

- `<>c__DisplayClass35_0` 约35.7%，栈叶为 `RpcContextService.ReceiveState.MoveNext`。该方法 server SyncRequest 分支的 `.Where(s => ... model...)` 捕获 model，编译器在入口生成 closure，即使当前是 Client 的正常增量也会分配。
- 两个 `ReadExactly` 状态机类型合计约51.8%。
- `NetMessageReader` 约12.5%，源码 ReceiveState 每帧 `new NetMessageReader`，未使用已有 reader 池。

Dictionary Host 权重1.81 MB：主要是 `_connections.Keys` 的复制视图和枚举器。没有再出现每次更新的新256项 Dictionary bucket/entry 存储。

下一优先级：将仅服务器使用的捕获/筛选分支抽到独立方法或无捕获循环；接收 reader 生命周期内租用/归还；TCP 改掉 byte[] 转 Memory 的 redundant async 包装并评估 ValueTask 读取。

## CPU 结论的边界

SampleProfiler 的主要栈为 Winsock Send/Receive、IOCompletionPoller、锁、LifoSemaphore、PollGC 等运行时/调度路径；当前没有证据把业务算法或 MessagePack 认定为 CPU 算法瓶颈。

很多 Managed/External sample 是等待/停驻栈，不能把样本数直接当成 CPU milliseconds 或占用率。此次证据足够给出**分配优化优先级**，不足以宣称这些等待栈“占用了百分之多少 CPU”。CPU 算法热点需更长固定负载/利用率或 ETW 的 running-thread 数据佐证。

## 重现

```powershell
dotnet build Tools/NetRpcPerformance/NetRpcPerformance.csproj -c Release -m:1
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile scalar --warmup 3000 --iterations 30000 --trace true --timeout-seconds 25 --output Artifacts/NetRpcPerformance/hotspots-scalar
dotnet build Tools/NetRpcTrace/NetRpcTrace.csproj -c Release
dotnet Artifacts/bin/NetRpcTrace/Release/net10.0/NetRpcTrace.dll --input Artifacts/NetRpcPerformance/hotspots-scalar/direct-scalar-128-client.nettrace --output Artifacts/NetRpcPerformance/hotspots-scalar/client-hotspots.json --preview
```

分析器 marker guards 通过、独立实际 EventPipe smoke 通过、基线 guards 12/12通过。采集启动/退出与复制有10s上限，进程仍由已有监督器明确拥有并清理。Windows .NET 10 loopback；不外推 Unity Mono、Player/AOT、WAN 或 UI。
