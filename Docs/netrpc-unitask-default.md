# 新 NetRpc：默认 UniTask、端到端链路与实测

2026-10-03。用户明确要求默认异步技术栈统一为 **UniTask / UniTask<T>**。本轮在 `Net.BITKit.Multiplayer.Hotspots` / `perf/netrpc-hotspots` 继续实现，未合入主目录、未提交。

**后续已整合主工作树**：上句与本页测试表保留 worktree 阶段的历史证据。当前主目录默认 UniTask，合入后的 .NET / Unity 编译和真实 smoke/lifecycle 已通过，见 [主工作树整合记录](netrpc-main-integration.md)。原始 worktree 的 `Artifacts/NetRpcPerformance/unitask-*` 证据仍位于 `D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer.Hotspots`；主目录新增 `main-integration-*` 测量，不把历史相对路径当作已经复制的附件。

## 默认链路

```text
业务 UniTask<T>
  → generated / woven UniTask<NetMessageBag?> receiver
  → UniTask 执行与原生 TCP/UDP/Relay 发送
  → runtime-local pooled IUniTaskSource<T>
  → Client UniTask<T>
```

没有在默认链路中转为 Task：

- `NetRpcReceiver`、`NetRpcResults.CompleteUniTask`、`RpcContext.Request/Request<T>`、`PublishStateAsync` 和内部派发/执行/状态同步使用 UniTask。
- UniTask 代理直接返回 `Request`，无需 async proxy 状态机。普通类编织直接调用 `FinishUniTask`，接收器直接调用 `CompleteUniTask`，移除历史 `FinishTask → AsUniTask` 和业务 `UniTask → AsTask → CompleteTask`。
- Native TCP/UDP、Relay 的发送、连接、接收/证明循环与 sidecar 使用 UniTask；netstandard2.1 的复用 SocketAsyncEventArgs 接收器使用 UniTask source。LiteNetLib Direct 的公开发送/连接/accept 与 poll 生命周期也迁移。
- `Samples/NetRpc`、Godot 业务契约/编织攻击、Session 和 UI 异步操作使用 UniTask。性能工具的 Scalar/DTO/Bytes/Fence 业务契约也迁移，进程控制、计量方式和业务断言保留。
- Core 引用 NuGet **UniTask 2.5.10**，仍为 netstandard2.1、无 UnityEngine 引用。源码 UPM Core/Transport/LiteNetLib asmdef 显式引用 `UniTask` assembly；包说明与 AGENTS 已写明默认栈。

## 兼容边界与升级

| API / 场景 | 当前处理 |
| --- | --- |
| `RpcContext.Request / Request<T>` | 默认 UniTask，消费一次 |
| `RpcContext.RequestTask / RequestTask<T>` | 显式 Task 兼容入口，保留 Task 取消状态与多次 await 语义 |
| `RequestValue / RequestValue<T>` | 显式 ValueTask 兼容入口，通过 Task 适配；不作为默认零分配表面 |
| Task / ValueTask authored RPC | 仍可生成/编织；在声明的兼容边界适配，receiver 仍返回 UniTask |
| `ITransport.Send / SendFast` | 返回 UniTask；自定义 transport/decorator 必须同步更新 |
| `IAsyncDisposable.DisposeAsync` | .NET 标准必须返回 ValueTask；Native/Relay/Session 提供 `DisposeUniTaskAsync`，标准接口只在销毁边界适配 |
| BCL socket/stream/semaphore/delay | 按系统返回类型直接 await，网络内部使用 ConfigureAwait(false)；不强制包装成 Task→UniTask→Task |
| Legacy B6 / TouchSocket | 既有 public Task/ValueTask API 不在本次新 backend 迁移范围 |

这是源码 API 与生成/编织输出升级，必须重新编译消费者、重新生成 proxy、重新编织普通 RPC。RpcMap method ID 包含返回类型；将业务声明从 Task<T> 改成 UniTask<T> 时，两端必须一起升级。头部和业务 payload 编码未改，但不能假设旧声明和新声明有相同 method ID。

无 HEAD 仓库已保存上一轮源文件于 `Artifacts/UniTaskBaseline/`（排除输出目录）；上一轮测量为 `final-{1,2,3}-*`。审查本轮差异使用 `git diff --no-index Artifacts/UniTaskBaseline/<目录> <目录>`。主目录有并行 Unity dispatcher/probe/生成器开发，合入只能逐文件迁移和解决实际 API 调用差异。

## continuation、上下文与生命周期

UniTask continuation 可以同步执行，且不自动流动 ExecutionContext / SynchronizationContext。

- 等待器换成每个 runtime 自有的 `UniTaskCompletionSourceCore<T>`；pending 上限 4096，每个结果类型缓存最多 256 个已消费等待器。
- 回复、取消、超时、断连、Dispose 先在 gate 内摘除 pending，**gate 外触发完成**；取消登记也在 gate 外。批量失败使用有界复用 batch，避免 inline continuation 重入正在枚举的集合。
- `GetResult` 后 Reset 清除结果/错误引用并推进 token version，再释放 cancellation registration 和回池；陈旧 token / 重复消费明确失败。
- invocation prefix 显式设置/恢复 `NetRpcCallContext.Current`，不能让未完成 UniTask 把 Host 的 ambient context 留在调用方。Task-authored 兼容业务仍由 .NET builder 流动其 context。
- UniTask-authored 异步业务需要跨 await 使用 sender/target 时，在入口保存不可变 `Current`，并把它传给后续业务 helper：

```csharp
public async UniTask<int> Operation()
{
    var call = NetRpcCallContext.Current ?? throw new InvalidOperationException("Missing RPC context");
    await SomeUniTaskOperation();
    return HandleForPeer(call.SenderPeerId);
}
```

Unity 对象访问仍由主线程 dispatcher 或显式 SwitchToMainThread 负责；UniTask 不自动把 socket callback 切到主线程。Core 的定时/证明/重连使用 BCL 时钟，不依赖 Unity PlayerLoop，独立 Host/Relay 可运行。

取消/超时不撤销已开始的远端业务；等待器完成不提前归还尚未结束发送的 frame。旧 request ID、连接世代、target/method、scope 的隔离继续保留。

## 相同真实网络负载：三轮未插桩

Windows .NET 10.0.11，native Direct，独立 Host/Client，并发 1。Scalar 预热 3000 / 测量 30000，Component 和 256 项 Dictionary 预热 2000 / 测量 20000。Host+Client 全线程精确分配，不扣 idle，不计预热/最终 JSON 输出；取三轮总量中位数。

| 负载 | 上一轮 Task/ValueTask 链路 B/op | 本轮 UniTask B/op |
| --- | ---: | ---: |
| scalar int RPC | 352.110 | **96.139** |
| ECS int Component | 0.146 | 0.275 |
| 256 项 Dictionary Set | 0.114 | 0.530 |

Scalar 比上一轮再下降 **72.70%**；比原始未插桩 **2262.921 B/op** 下降 **95.75%**。本轮 Client 三次仅分配 2576 / 3600 / 2536 B，主要是窗口固定开销；不是每个 RPC 创建 Task。Host 剩余约 96 B/op 为调用上下文。

状态负载每轮仍各收到 **20000 次应用回调**，最终值/版本正确。状态的少量固定控制/调度开销有波动，不宣称本轮状态 B/op 比上一轮进一步下降或整体零 GC。

| 负载 | 本轮三次 Host+Client 精确总 B |
| --- | --- |
| Scalar | 2883072 / 2884160 / 2884992 |
| Component | 5672 / 5496 / 5496 |
| Dictionary | 10656 / 10592 / 9016 |

证据：`Artifacts/NetRpcPerformance/unitask-{1,2,3}-{scalar,component,dictionary}/`。Scalar 本轮 Client p95 中位数约 0.143ms，上一轮约 0.1205ms；这几批 loopback 延迟不支持 CPU/吞吐变快的结论，本轮明确验证的是分配下降和行为正确。

64 并发 / 10000 次真实 RPC：Direct **98.173 B/op**、Relay（含 Relay 进程）**97.768 B/op**，全部结果和帧计数断言通过。证据：`unitask-concurrency-64/`。Direct/Relay 九类负载、集合 1/128/256 项矩阵 **26/26**，证据：`unitask-matrix/`。

## 最终 EventPipe

`Artifacts/NetRpcPerformance/unitask-trace-scalar/`：marker 窗口成立，Host/Client eventsLost=0、truncated=false。

- Client：窗口内 **0 个 AllocationTick**；精确分配仍为 **2696 B**，不能把无抽样误写为绝对零分配。
- Host：28 个有效样本，权重 2.97 MB，精确 2.88 MB；类型只剩 OneElementAsyncLocalValueMap、ExecutionContext、NetRpcCallContext。
- 原 Task<int>、Task<NetMessageBag>、ValueTaskSourceAsTask<int> 和 continuation work-item 抽样热点均未再出现于该 scalar 窗口。真实 woven IL 还断言 FinishUniTask/CompleteUniTask 且无 AsTask/AsUniTask/FinishTask 中转。
- 变长 DTO/byte[] 仍有反序列化对象/数组，异常有独立成本；单标量结果不外推任意类型或 Unity Mono。

## 验证与 Unity 读回边界

- Solution Release build 通过（Core netstandard2.1、Transport/LiteNetLib 双目标）。
- 完整 suite **295 通过 / 0 失败 / 2 原有 benchmark 跳过**；NetRpc 定向 **42/42**，LiteNetLib **15/15**，performance guards **12/12**。
- 新增 UniTask 回归 5 项：default public/receiver 形状、真实 async completion/fault 与上下文恢复、inline continuation 重入与陈旧 token、UniTask/Task cancellation status、128 并发 WhenAll 跨池世代。原有借用 buffer 立即覆写、超时/取消仍持有 send loan、旧回复/迟到 send failure、连接替换回归继续通过。
- 强制加载 netstandard2.1 Transport 的真实 socket 兼容 **4/4**；Direct/Relay sample 均 PASS。Godot 客户端另做编译检查，未重新跑可见窗口。
- TRX：`Artifacts/TestResults/UniTask/`、`unitask-netrpc.trx`、`unitask-litenetlib.trx`、`unitask-netstandard-sockets.trx`、`unitask-performance-guards.trx`。

按 Project B codebase / Funplay workflow 通过 MCP 只读核对：实际 Project B 为 **Unity 2022.3.62f3、Edit Mode**，已加载 UPM **com.cysharp.unitask@2.5.10** 的 `UniTask.dll`。当前 multiplayer resolvedPath 仍为主目录 `Net.BITKit.Multiplayer/Src`，并非此 worktree；本轮没有切换包引用、没有修改宿主、没有进入 Play。故新 worktree 的 Unity import/compile、dispatcher 行为、Mono GC、Player/IL2CPP 仍未验证。

## 重现

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release -m:1
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build
dotnet test Tests/NetRpcCompatTests.csproj -c Release --filter "FullyQualifiedName~RealTcpAndUdp|FullyQualifiedName~AuthenticatedUdp|FullyQualifiedName~TcpFrameLimit|FullyQualifiedName~CompatibilityRun"
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile scalar --warmup 3000 --iterations 30000 --timeout-seconds 25 --output Artifacts/NetRpcPerformance/unitask-recheck
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport both --profile scalar --concurrency 64 --warmup 1000 --iterations 10000 --timeout-seconds 25 --output Artifacts/NetRpcPerformance/unitask-recheck-64
```

下一步是逐文件合入并更新主目录并行 Unity dispatcher/probe 的 receiver、transport、调用签名，再用现有 Editor 做最小编译与调用/生命周期读回。残余 scalar 分配已迁到调用上下文，后续优化应有新的 profiler 证据，不能破坏 sender、scope 或异步边界。
