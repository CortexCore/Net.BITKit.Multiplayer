# 网络模块本身的 GC 基准（2026-10-06）

本页使用独立 .NET console、真实 native TCP/UDP、生成接口代理和实际编织的普通类 RPC。没有 Godot、UI、动画、角色业务或输入回放。此前整款 3D demo 的 88.8% 客户端分配下降不是网络模块指标，不纳入本基准。

## 版本与复现边界

- 源码基点：`37d817a99be370518056139dafd590b0ad2af08b`
- 保留此前尚未提交的同步 worker 退出修复和生命周期测试/文档；没有覆盖或回滚它们
- 重用已有 `Tools/NetRpcPerformance`。修复其过时的 Host DI 注册，再扩展为两个独立 Client 进程；不是新建游戏或替换网络协议
- SDK 10.0.401，运行时 .NET 10.0.12、Debian 13 X64、9 个可见处理器、workstation GC。Native Transport 使用 net8.0 构建在 .NET 10 进程运行；详情在每份角色 JSON 与 `dotnet-info.txt` 中记录
- 原始缺失 DI 注册导致的失败保留在 `original-smoke`；修复后的单 Client 兼容性基线另存，不与三进程重复基线混算

## 固定矩阵

三个独立角色进程：一个 Host、两个 Client。每个场景重新创建进程。所有场景重复三次。

- 每个 active worker 目标 300 次/秒，绝对截止时间闭环节拍；预热 300 次，测量 600 次
- RPC：两个 Client 依次发起各 600 次，Host 完成 1,200 次。两个连接全程存在，但这不是同时双 Client 压测
- 状态：Host 修改 600 次并扇出两端，检查每端实际回调、版本和最终值
- RPC DTO/byte[] 使用预分配的 256 B 发送负载；接收端真实反序列化分配保留
- List/Dictionary 各固定 128 个元素，每次写入不同值；没有将 no-op 当更新
- idle：连接存在，但无自动同步 worker；idle-sync：默认 100 ms 同步、2 s 强制快照，分别测量 3 s
- control：连接存在，Host 执行 600 个同样节拍，但不发业务消息；它只衡量节拍/控制开销，不是可从 RPC 中扣除的匹配对照

RatePacer 使用同步 Sleep，UniTask 续体可能在接收回调线程运行。该矩阵是闭环固定节拍的分配预算，不能把其 RTT 或吞吐当独立开放环负载发生器的容量极限。达到的实际速率由各进程真实窗口/完成数计算，未用目标速率冒充实测速率。

## 计量契约

使用 `GC.GetTotalAllocatedBytes(true)`，覆盖每个角色进程所有托管线程，并记录 Gen0/1/2 增量。预热、连接准入、代理/业务解析、输入/采样数组分配在窗口外；所有角色先冻结结束戳和 transport 计数，再序列化报告。监督进程不计入网络预算。

原始数值包含角色侧短控制命令、少量收尾等待和回调。没有 idle/control 相减，没有把 0 次 GC 当成 0 B 分配。每个角色窗口略错开；汇总 B/s 是各角色 B/s 之和，B/op 是各窗口分配总和除以实际 Host 完成数。

RPC 分母为已验证 request/result 或实际 woven void Host 调用；void 使用单独的应用 fence，没有虚构每条 void 的 ACK。状态分母是 Host 修改次数，包含两 Client 的网络处理；它不是“每个 Client 收到一次”的分母。

计数 decorator 只在成功完成发送后记数。发送/接收字节是 NetRpc 应用帧字节，包含 NetRpc 头，不含 TCP 长度前缀、UDP proof/token、IP/socket 元数据或重传。未测量 native 内存、RSS 或 packet-capture 带宽。

UDP 单独记录实际发送帧、接收帧、应用回调。每端额外发送一次最终强制修复快照，并计入窗口。不能从发送数推断成功到达。未限速的单 Client 诊断曾只有 1,784/2,000 次应用回调，已原样保留；下面受控双 Client 三轮均为 600/600 每端回调。

## 优化前结果

`Artifacts/NetworkModuleBaseline/before/r1..r3`，33/33 场景通过。以下为三轮中位数，括号为 min–max，原始 CSV/JSON 保留完整角色数据。

| 场景 | 总托管 B / Host 操作 | 分母 |
| --- | ---: | --- |
| woven void | 99.65（99.06–99.78） | 1,200 次实际 Host 调用 |
| generated scalar RPC | 100.28（98.29–101.02） | 1,200 次 request/result |
| DTO + 256 B | 411.69（410.21–416.32） | 1,200 次 request/result |
| byte[] 256 B | 381.97（381.91–384.14） | 1,200 次 request/result |
| UDP component | 16.81（15.93–20.73） | 600 次 Host 修改，两端各 600 回调 |
| reliable scalar state | 103.01（100.69–140.80） | 600 次 Host 修改，两端各 600 回调 |
| List index set | 23.01（20.01–23.28） | 600 次 Host 修改，两端各 600 回调 |
| Dictionary key set | 22.04（21.15–23.40） | 600 次 Host 修改，两端各 600 回调 |

三个非业务窗口的角色合计中位数：idle 536 B/s；idle-sync 2,598 B/s；control 645 B/s。窗口只有数秒；这些轮次 Gen0/1/2 都没有新增，但所有场景都有托管分配。

Scalar state 的 Client 分配存在明显轮间噪声，不能将最好的单轮当确定预算。Host 却三轮均为 19,632 B / 600 次，其中可重现的 32 B/次来自 scalar 比较缓存。

## 已验证的最小优化

`NetRpcState` 每次 scalar 编码变化都用 `ToArray()` 替换仅供比较的私有 `LastValue`。独立的同步、无 peer、无 timer 测试在 1,000 次相同编码长度的不同值上准确测得 32,000 B。该微诊断只归因 Host 编码/缓存，不代表整个网络模块。

改动只在已有 `_stateGate` 内、编码长度相同时复用私有缓存；长度变化时重新分配。缓存从不作为借出的发送缓冲，所有版本递增、重复跳过与强制快照语义保持。另有整数编码增长/收缩、重复值、强制快照版本和旧帧内容测试。独立只读审阅确认两个调用点均持有既有锁，发送帧另有独立拥有的存储，延迟发送不会被这个缓存复用改写。

该改动已通过两个定向测试：原来失败的 1,000 次缓存更新从 32,000 B 降为 0 B，编码/版本行为测试通过。不能把这个局部微诊断的 0 B 称为网络模块零分配。

最终完整网络对照使用 `after-confirmed/r1..r3`：33/33 通过，驱动脚本退出码明确记录为 0。每轮 Host scalar-state 的分配从 19,632 B 降到 432 B，600 次更新**准确减少 19,200 B，即 32 B/次**；Host 原始窗口预算 32.72 → 0.72 B/次。两 Client 每轮仍各收到 600 次更新，最终值、版本和实际帧检查都通过。

| 场景 | 优化前中位 B/op | 最终优化后中位 B/op（min–max） |
| --- | ---: | ---: |
| woven void | 99.65 | 99.42（99.06–99.42） |
| generated scalar RPC | 100.28 | 99.29（99.24–101.66） |
| DTO + 256 B | 411.69 | 412.45（411.63–413.16） |
| byte[] 256 B | 381.97 | 378.27（378.26–381.11） |
| UDP component | 16.81 | 18.45（16.75–23.04） |
| reliable scalar state | 103.01 | 70.59（67.99–70.85） |
| List index set | 23.01 | 21.73（21.08–21.85） |
| Dictionary key set | 22.04 | 22.19（20.07–23.53） |

Scalar-state 的三进程合计中位数下降约 31.5%，但可靠归因是 Host 的固定 32 B/次；其他未针对的行上下波动不算这个优化的收益。最终 idle/control 分别约 536/643 B/s；idle-sync 中位数从约 2,598 上升到 3,661 B/s，尽管每轮仍为相同的 12 个 TCP 与 4 个 UDP 帧。短窗口后台分配有明显变动，未将这行藏掉，也不宣称所有场景都改善；默认同步 worker 的定时分配未被本补丁消除。所有这些短窗口仍是“有分配、没有新增 GC 代计数”。

第一次优化后矩阵 `after/` 也完成了全部 33 个 PASS case/JSON，但外层工具返回 127。期间修改过导出 ZIP 的复现脚本，具体外层退出原因未确认，冻结的测量 DLL 没有变化。该批数据完整保留但不作为上表主对照；随后保持脚本不变重跑 `after-confirmed/`，记录完整 PASS 与退出码 0，避免拿不明确的驱动状态宣称成功。

## Transport-only 分层诊断

`Tools/NetRpcTransportBaseline` 是小型配套诊断，直接调用现有 `TcpTransport` 字节边界；不经过 RPC codec、DI 或状态 Runtime。一个进程拥有两条 Host–Client 连接，共四个 endpoint，每 tick 四个方向各发送一次。该全线程分配总数无法再按 Host/Client 归因，也不是完整模块基线。

Primary：300 ticks/s，即目标 1,200 sends/s；256 ticks 预热、600 ticks 测量，32/256/1024 B、TCP/UDP 各三轮，另三轮同节拍无发送 control。21/21 场景通过，每个 transport 轮次准确发送并收到 2,400 帧，无缺失、重复、非法负载、late delivery 或 fault。所有窗口 GC 代计数增量为 0。

| 场景 | 三轮原始托管分配 B / 整个窗口 |
| --- | --- |
| control | 0 / 48 / 96 |
| TCP 32 B | 56 / 0 / 0 |
| TCP 256 B | 280 / 280 / 0 |
| TCP 1024 B | 0 / 0 / 0 |
| UDP 32、256、1024 B | 各 0 / 0 / 0 |

这里的 0 B 是这些已预热、有限窗口的实测，不是网络模块整体零分配保证。control 的非零波动原样保留，未做相减。TCP/UDP 发送都等待真实完成，输入提前准备，接收端校验 payload/edge/sequence，统计收到的实际字节和帧，不用发送数冒充投递数。Native RSS 与操作系统内存未测量。

初始未限速的 UDP warmup 曾在 1024 B 一端只到 101/128 帧，工具按设计失败并保留证据。将窗口外 warmup 按 1,000 ticks/s pacing 后重新验证通过，正式测量负载没有改变。这是 harness 修正，不是隐藏 UDP 失败或改用 TCP。

Supplementary：60 ticks/s（目标 240 sends/s），180 ticks 测量约 3 s，同样三个 payload、两个 channel、三轮加 control，21/21 通过。每个 transport 轮次准确投递 720/720 帧。原始三轮 B：control 0/48/0，TCP32 0/32/0，TCP256 0/0/0，TCP1024 1048/0/0，所有 UDP 为 0/0/0。两个重复矩阵合计 56,160 次实际发送和独立校验的唯一接收，未发生 fault、缺失、重复、非法或 late receive。

## 验证记录

- Benchmark guard：21/21 通过
- Host + 两 Client 小规模 smoke：13/13 通过（集合长度 1 和 128）
- 单 Client Direct/Relay 功能兼容：26/26 通过；与其他回归同时运行，不作为性能数字
- Transport-only guard：8/8 通过；两个重复矩阵 42/42 通过，源码/程序集 15 个 hash 在采样前后保持一致
- Scalar cache 定向测试：2/2 通过；优化前准确失败证据与优化后 TRX 均保留
- 完整 solution Release 构建通过，0 error、1 个既有 CS0067 warning；完整测试原样重试为 207 通过 / 0 失败 / 1 个既有可选 benchmark 跳过
- 首轮完整测试有 1 个既有 UniTask 测试的 5 ms 时序断言失败（预期 Pending，观察到已 Succeeded）；未改这个测试或隐藏失败。该单项重试和随后完整重试通过，首次失败与重试 TRX/日志均包含在证据中

## 复现

```bash
dotnet build Tools/NetRpcPerformance/NetRpcPerformance.csproj -c Release -m:1
dotnet test Tools/NetRpcPerformance.Tests/NetRpcPerformance.Tests.csproj -c Release -m:1
bash Tools/NetRpcPerformance/run-network-baseline.sh baseline
python3 Tools/NetRpcPerformance/summarize-network-baseline.py Artifacts/NetworkModuleBaseline/baseline
```

生产性能对比应使用相同构建、负载和环境；源码/程序集 SHA256 与配置随证据保存。测试期间没有其他由本任务启动的 benchmark 并行负载，但共享云环境并非专用物理机，进程后台工作和短窗口噪声仍存在。结果只覆盖当前 Linux/.NET native Direct 路径，不外推 Unity Mono、IL2CPP、移动端、公网或高并发人数。
