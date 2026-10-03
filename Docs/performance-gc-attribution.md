# 性能与 GC 分配归因 — 2026-09-27

**本页是替换前的归因基线。后续已通过 ITransport/DI 改用原生 UDP，去除下述 TouchSocket 接收分配路径。** 新旧同负载 A/B 见 [transport-validation.md](transport-validation.md)，使用方式见 [transport-guide.md](transport-guide.md)。本页原始数字保留为历史证据。

## 结论

**客户端最大的分配来源已定位到 TouchSocket 4.3.9：UDP 接收循环每轮新建 65,536 字节数组。** 在本次真实 Release 客户端采样中，该方法占估算分配的 **Direct 76.49%、Relay 73.60%**。这不是依据代码猜测：有真实进程分配堆栈、部署依赖哈希和对应 DLL 的反编译/IL 三层证据。

Host 的重点则是可靠 RPC 的 JSON 接收/响应路径，以及位姿分批测量、编码和扇出。Arena 的视图复制、插值、报告确实分配，但不足以解释客户端的主要分配量。**换显示引擎、但保留同一个 TouchSocket 接收实现，不会消除这项成本。**

当前 35 秒本机负载中，最长完整 GC 暂停为 **1.37 ms**；相比之下，更新线程上的诊断采样曾耗时 **9.89 ms**、报告写入 **5.43 ms**。这两类同步耗时不能当作 GC 暂停，也不是纯 CPU 时间。要优化分配和周期性更新停顿，需要分别处理。

本轮交付的是可重复的性能归因和优化顺序；下面数字是当前实现的基线，不是优化后的收益预测。

## 1. 方法与证据

环境：Windows x64、Release、目标进程 coreclr **10.0.11**。同一 Host、三名 Client，20 Hz 权威模拟，Alice 开火、Bob 静止、Charlie 移动；全部为 headless，以排除 Raylib 绘制。Direct/Relay 场景先后执行，未互相并行争抢运行时间。预热并确认全部成员就绪后，分别从外部进程附加 Host/Alice，采集约 35 秒。

| 场景 | `Artifacts/ArenaRuns/` 下的正式证据 |
| --- | --- |
| Direct | `20260927-120937-f9f48d` |
| Relay | `20260927-121125-5fea65` |

每个目录有：

- `profile-host.nettrace` / `profile-alice.nettrace`：原始 EventPipe 事件。
- 同名 `.etlx`：rundown 解析后的方法堆栈，可进一步独立核查。
- 同名 `.json`：分配类型、排他子系统、包含方法、代表性完整堆栈、GC 暂停和其他运行时暂停。
- `profile-window.json`：附加前/停止请求后的原始 GC 与阶段计数器快照、UTC 边界、部署模块哈希。
- 各节点报告、日志及 E2E `summary.json`：业务连通、生命周期与正常退出结果。

计量口径：

1. **EventPipe `GCAllocationTick` 是抽样估算**，使用事件的 AllocationAmount64/AllocationAmount；不是逐对象精确字节数。四条 trace 的分配样本均解析出托管堆栈。`DroppedEventSignals=0` 只表示没有观察到丢失标记，不能证明无丢事件。
2. 排他归属按最近的项目方法分组；“外部”并不表示堆栈未解析。**包含方法之间会重复计算同一份分配，不能相加。**
3. 同步阶段使用 `GC.GetAllocatedBytesForCurrentThread()`，每段必须在同一线程开始/结束，不跨 await；阶段互不嵌套，计时为 Stopwatch **经过时间**，包含阻塞和调度。
4. 阶段和整进程快照仅近似对齐：报告约 250 ms 刷新，而 `Gc.Final` 约每秒采样。只能按各自边界求差，不能把阶段剩余值精确分配给某个线程。
5. 只分析 trace 开始到 **请求停止**之间的事件；后续 rundown 用于符号解析，不计入稳态负载。跨窗口的暂停不计入。Profiler 自身在另一个进程，其分配不进入目标计数。
6. 本轮没有 CPU 抽样、完整帧时间分位数、GPU 分析或 Unity Player 测量；这里的“热点”首先指分配来源。

## 2. 全进程基线

MB 使用十进制。约 35 秒 EventPipe 估算：

| 节点 | 估算采样分配 | 采样速率 | GC 暂停次数 | 合计暂停 | 最长暂停 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Direct Host | 71.74 MB | 2.048 MB/s | 5 | 4.488 ms | 1.366 ms |
| Direct Alice | 118.70 MB | 3.389 MB/s | 9 | 5.487 ms | 0.967 ms |
| Relay Host | 84.01 MB | 2.399 MB/s | 7 | 5.916 ms | 1.261 ms |
| Relay Alice | 124.55 MB | 3.556 MB/s | 10 | 6.004 ms | 1.124 ms |

GC 暂停只取 CLR 的 `SuspendForGC` / `SuspendForGCPrep` 开始，到 `RestartEEStop` 的完整区间。四个进程窗口分别另有 **34 次 `SuspendOther`**，合计 0.628–0.783 ms；它们已单独列出，不能混入 GC 次数。35 秒内样本很少，不能据此承诺长期 P99 或峰值负载停顿。

附近整进程分配计数器的独立核对：

| 节点 | 计数器窗口 | 分配计数器增量 | Gen0 / Gen1 / Gen2 增量 |
| --- | ---: | ---: | --- |
| Direct Host | 34.522 s | 69,746,456 B | 6 / 1 / 0 |
| Direct Alice | 34.585 s | 116,511,440 B | 9 / 0 / 0 |
| Relay Host | 35.486 s | 85,370,136 B | 6 / 0 / 0 |
| Relay Alice | 34.617 s | 122,578,896 B | 10 / 0 / 0 |

暂停区间数量和 `CollectionCount` 不必相等：采样边界不同，一次回收也不保证只有一个运行时暂停区间。

## 3. 热点一：TouchSocket UDP 接收缓冲

实际 Arena `.deps.json` 选择 TouchSocket 4.3.9 的 **net10.0** 资产；不能根据适配器项目的 net8.0 目标误写成实际依赖资产。部署 `TouchSocket.dll` SHA-256：

`7E7E98EB795850A079052911A6DDFC84FD6652E5DCB7A221BEB2178B9D9A1751`

对应接收循环的关键代码：

```csharp
while (true)
{
    Memory<byte> memory = new Memory<byte>(new byte[65536]);
    // ...await receive...
    // ...await HandleReceivingData(...)...
}
```

| Alice 客户端 | `UdpSessionBase.RunReceive` 叶方法估算分配 | 占全部估算分配 |
| --- | ---: | ---: |
| Direct | 90.79 MB | **76.49%** |
| Relay | 91.67 MB | **73.60%** |

完整堆栈和反编译相互印证。64 KiB 是**每轮接收数组容量**，不是网络包大小，也不能用采样估算字节数反推精确包数。该数组本身低于通常约 85 KB 的 LOH 阈值，不应因此误诊成大对象堆问题。

我们在 `Src/TouchSocket/UdpLane.cs` 的发送缓冲和 Relay 转发复制上做的池化发生在下游，不能消除这个上游分配。现版本没有可配置该循环容量/复用策略的公开开关，私有接收方法也不能直接 override。

**优先优化方向：**在可维护的依赖源代码补丁或上游版本中，每个 receive worker 持有一个完整 64 KiB 缓冲，等待本次回调处理完成后复用，退出时归还。不要为了减少分配把容量直接改成 1200；那会引入超大包截断识别问题。详细 IL、复现和安全实验边界见 [依赖证据](touchsocket-udp-allocation-evidence.md)。此方案尚未实施或 A/B，不能把约 74% 采样占比直接写成已实现的性能改善。

## 4. 热点二：Host 可靠 RPC 与位姿批次处理

### 可靠路径

位置已经使用 MemoryPack，但移动输入、开火、可靠生命周期、Health 与返回/确认仍走可靠路径。相关代码：

- `Src/Runtime/RpcRuntime.cs`：`Encode`、`Decode`、`ToToken`、`FromToken`、`Handle`、`ObserveSend`。
- `Src/TouchSocket/RelayProtocol.cs`、`RelayWires.cs`：可靠 Relay 的包装和派发。

Relay Host 的包含分配堆栈中：`RpcRuntime.Receive` **40.79 MB**，其内 `Decode` **20.19 MB**，`ObserveSend` **21.78 MB**。代表堆栈落在 Newtonsoft 的字符串解析、StringBuilder 扩容及对象序列化。**这些包含值重叠，不是三项可相加的独立预算。**

这提示第二优先级应是可靠高频调用的编解码、中间字符串/JToken、方法元数据和 Relay 包装成本。不能把“Unreliable 已用 MemoryPack”理解成全部网络消息都已二进制化；迁移可靠协议需独立处理兼容性，不能以优化名义取消 Health 等可靠语义。

### 位姿路径

`Samples/Arena/Game/ArenaSession.cs` 的 `SendBatches` 每增加一个候选元素，就调用一次 `MeasureUnreliableCall` 完整测量，然后再调用真实发送。测量路径又包含属性/参数反射、`MethodId`/`TypeId` 构造、装箱和编码。Host 的整个同步 `MotionSend` 窗口分配 **16.60 MB（Direct）/16.92 MB（Relay）**。

这包括采样、分批、测量、编码、同步本地执行和发送启动，**不是 MemoryPack serializer 单独分配**。可优先实验缓存方法/参数/稳定头部信息，并减少候选批次的重复完整编码，同时保留真实字节上限验证。当前分组数据不能给出“其中某一行占多少”的精确数值。

此前 `Datagrams.PositionSendAllocatedBytes` 只包围 `publish(...)`，**不包含前面的批次测量循环**；新阶段计数器覆盖整个 `SendMotion()`。两者不是相同口径。

## 5. Arena 自身究竟占多少

以下为 `profile-window.json` 前后阶段计数器差值，单位 MB；窗口约 35 秒，阶段不互相重叠。后台收包及 await 后转到其他线程的工作不在表内。

| 同步阶段 | Direct Host | Direct Alice | Relay Host | Relay Alice |
| --- | ---: | ---: | ---: | ---: |
| GetView 视图复制 | 0.811 | 0.776 | 0.795 | 0.758 |
| 插值/显示快照构造 | 4.868 | 4.535 | 4.784 | 4.447 |
| JSON 诊断报告写入 | 2.663 | 2.559 | 2.495 | 2.541 |
| 输入提交（含同步可靠 RPC 路径） | 0 | 5.504 | 0 | 7.344 |
| Datagram 统计报告 | 0.446 | 0.223 | 0.437 | 0.218 |
| GC/进程状态采样 | 0.149 | 0.141 | 0.150 | 0.145 |
| 权威步进（含同步 Health 回调） | 0.733 | 0 | 1.074 | 0 |
| 整个位姿发送阶段 | 16.600 | 0 | 16.920 | 0 |
| Tick 内可靠生命周期发布 | 5.127 | 0 | 6.750 | 0 |
| **全部受测同步阶段合计** | **31.398** | **13.738** | **33.406** | **15.452** |

报告元数据更新分配为 0，其他 App 阶段本窗口接近 0。小数有四舍五入，精确字段在原始 JSON。

Alice 的 **视图＋插值＋报告**仅约 **7.87 MB（Direct）/7.75 MB（Relay）**，而附近全进程窗口分配约 116.5/122.6 MB。这足以否定“主要是 Arena 显示层”的假设，但因为窗口边界不同，不将它包装成精确到小数点的全进程百分比。输入阶段包含网络调用，也不能全部算成纯 UI 开销。

## 6. 诊断代码本身的更新线程开销

`ArenaController.Update` 同步调用 `ArenaGcSampler.Sample`；后者采集精确总分配量、堆信息以及 `Process.GetCurrentProcess()/Refresh()/WorkingSet64`。此外报告约每 250 ms 同步 JSON 序列化并原子替换文件。

Relay Alice 的受测窗口：

- GC/进程状态采样阶段累计 **247.18 ms**；多数帧因间隔不足直接返回，实际采集约每秒一次。
- 报告写入阶段累计 **270.70 ms**。
- 整个预热后会话的单次阶段最大值分别为 **9.8946 ms**、**5.4295 ms**，来自原始 `alice.json` 的 `MaxElapsedTicks / StopwatchFrequency`。这是全会话最大值，不冒充窗口内最大值。
- 同一 35 秒 trace 中真正最长 GC 暂停为 **1.124 ms**。

因此周期性的更新停顿不能一概归罪于 GC。计时包含操作系统等待、线程抢占等，没有据此断言哪一个系统调用消耗了多少 CPU。后续应把昂贵进程诊断改成显式开发选项、降频或移出游戏更新线程；异步报告需采用有界的 latest-snapshot/单写入者策略，不能换成无限 Task 队列。

## 7. 优化优先级与验收条件

1. **TouchSocket 接收缓冲复用**：预期收益最大且根因明确。先做依赖侧隔离补丁/A-B，保留完整接收容量及等待回调的所有权语义；验证回收、重绑、退出和超大包拒绝，再比较同负载分配率。
2. **诊断路径的更新线程阻塞**：优先改善周期性停顿风险；关闭/降频/后台化后保持可选、固定上限和可观察错误。
3. **Host 可靠 RPC 与元数据缓存**：缓存稳定反射结果/标识，减少中间对象；可靠 codec 改动单列版本兼容验收。
4. **位姿批次重复测量**：精确长度估计或有界探测策略，避免逐元素从头编码；超过 900 B 仍须拒绝，不做隐藏 TCP fallback。
5. **Arena 快照与插值复用**：确有空间，但对当前 Client 总分配的影响远小于第一项。Unity 最小适配使用其实际对象模型验证，不为降低一个 Sample 数字先重写引擎。

每项只改一个因素，重复相同 Release 负载；同时对比实际包数、业务状态、阶段/全进程分配、GC 暂停与关闭后租用计数。采样占比不能当作确定的可消除字节数。不能通过降低模拟频率、隐藏诊断开销或取消可靠性来制造改善。

## 8. 复现与数据质量

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay

# 单独附加已有进程；原始trace、解析文件及JSON在指定路径旁保存
dotnet run --no-build -c Release --project Tools/Performance/Arena.Performance.csproj -- --pid 12345 --duration 35 --outfile Artifacts/profile.json
```

E2E `--profile` 自动启用 App 的 `--allocation-phases`。也可单独使用 E2E 或 App 的 `--allocation-phases`，不启用外部 trace。默认普通模式不记录阶段计数；历史 GC 报告采样逻辑保持原样。阶段数据为固定数量计数器，不存逐帧历史、不强制 GC。

- Release 完整构建零警告/错误，**132 项测试通过**（App 阶段计数器新增 2 项）；正式 Direct/Relay profiling E2E 均验证完整业务生命周期、报告有效性、trace 结束和正常清理。
- 早期 `20260927-120027-6c2ba1` / `20260927-120215-38e63a` 的 typed JSON 快照把 getter-only 阶段计数器反序列化为 0。已改为从同一次原始报告读取克隆 JSON，并加入 Sampling/阶段计数增长断言。**本报告只用修复后的两个正式窗口计算阶段差值。**
- Relay 正式窗口中，Host 的某次 `GC.GetTotalMemory(false)` 原始近似值为负数。该值没有被伪装成 0 或“回收收益”；本报告不使用它判断 live heap/泄漏，只用分配计数器和事件归因。需要精确存活堆结论时应另采堆转储/稳定 GC 后快照。
- “可回收/有界”的原有租用测试结论保留，见 [V4 GC 报告](v4-gc-report.md)。本轮没有做泄漏根对象分析、长时间高人数压力、Relay 独立进程分配采样、Unity/AOT 或目标设备帧时间验收。
