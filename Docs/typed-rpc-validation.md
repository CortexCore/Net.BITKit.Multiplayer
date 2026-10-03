# 三项 RPC 热路径改造验收 — 2026-09-27

## 构建与完整回归

Release solution build：**0 警告、0 错误**。完整测试 **182/182 通过**：Core 71、Transport 13、Datagram 20、Relay 20、Lobby 9、Game 30、App 19。Core 的嵌套上下文/指纹修正还运行过 Debug 69 项回归；最终新增两项测量测试在 Release 通过。

随后进一步加入了零分配硬断言，并单独运行 `TypedHotTests`，**12/12 通过**；这些断言只修改测试，没有改变下述实际进程使用的运行时构建。

覆盖实际 woven 类型、接口/具体实例、两种 delivery、数值 ID/指纹、直接 handler IL、void 无 reply/waiter/fanout ACK、Task<T> 结果/错误/取消、权限与 ready barrier、并发/重入、源数组复用、借用接收内存、延迟/失败/关闭时的租用、128 在途上限及所有权恢复。

## 纯生成路径：预热后 0 B 的准确范围

`Tests/TypedHotTests.cs` 使用同步 borrowed-memory 测试线缆，对实际编织程序集执行调用。`GC.GetAllocatedBytesForCurrentThread()` 只在全部处理同步完成的被测线程上求差，不跨 await。生成入口/接收器还检查 IL 不含 object-array newarr、参数 box 或 MethodInfo.Invoke。

| 场景 | 循环数 | 测得分配 |
| --- | ---: | ---: |
| Reliable 标量 void，仅发送端 | 2,000 | **0 B** |
| Reliable 标量 void，发送＋同步接收，含授权委托 | 2,000 | **0 B** |
| Unreliable 标量 void | 2,000 | **0 B** |
| Unreliable 固定 unmanaged struct void | 2,000 | **0 B** |
| 固定 struct 远端／Host 本地 | 各 1,000 | **0 B** |
| Host All＋两个接收端，Reliable／Unreliable | 各 1,000 | **0 B** |

以上为当前 .NET 10.0.11、预热后、正常容量的同步测试结果，已有精确零字节断言。**不代表 native socket、异步调度、可变长度数组/DTO 或整个 Arena 进程零 GC。** 例如 30-pose 数组的接收值仍需拥有数组，早期 typed 测量约 625 B/send；不是 wrapper 又创建了 object[]。

另外，显式测包长接口使用相同预先装箱的一段参数，在 5,000 次预热测量中由 **15,961,208 B（3,192.24 B/次）降为 0 B**。修复了重复创建特性/参数元数据、方法名字符串和 target SHA 的开销。实际 Arena 在进入该接口前新创建的参数 boxes 不包含在这个零分配口径里。

## 审查中修正的问题

- Hybrid 调用上下文原先会让外层 AsyncLocal 覆盖内层同步 RPC；现在同步帧关联其 async parent，async→void→async 的 sender/target 在 await 和异常后正确恢复。
- TargetKey 原先使用 NUL 分隔拼接，不同含 NUL 的 tuple 可能得到相同输入；改为长度前缀严格 UTF-8，有真实不同 target 路由测试。
- 指纹补入枚举 underlying/value 和 struct layout/packing/size/offset，避免布局变化仅凭类型名被当成相同协议。
- 收发池保持字节租用直到真实 underlying send 结束；Relay close 不提前释放 active frame，已覆盖延迟/故障/队列溢出。
- 最初 typed 版本只覆盖 Reliable，集成审查后补齐普通 woven Unreliable，包括相同的 typed 生成器、指纹/通道验证和实际测包长。

## 真实进程对照

Windows x64、Release、native UDP，使用原 E2E 三 Client bot、20 Hz 模拟，Host 和 Alice 各采约 35 秒。所有 profile 场景顺序运行，每组一次，没有失败重试。原始 `.nettrace`、`.etlx`、JSON 和 profile-window 均保留在 `Artifacts/ArenaRuns/`。

| 路线 | binary v3 基线 | typed 中间版本 | 最终 typed v4 |
| --- | --- | --- | --- |
| Direct | `20260927-155738-e8ddbf` | `20260927-222425-a4b9ea` | `20260927-224946-bfac0a` |
| Relay | `20260927-155925-da3ad0` | `20260927-222628-7f282c` | `20260927-225135-08c7a6` |

速率为 profile-window 同一节点的进程 `AllocatedBytes` 差 / `ElapsedMilliseconds` 差；有报告/计数器采样边界误差。MB 使用十进制，不能当作精确纯 Transport 分配。

| 节点 | binary v3 | typed 中间版本 | 最终 typed v4 |
| --- | ---: | ---: | ---: |
| Direct Host | 1.051 MB/s | 0.722 MB/s | **0.407 MB/s** |
| Direct Alice | 0.501 MB/s | 0.277 MB/s | **0.328 MB/s** |
| Relay Host | 1.279 MB/s | 0.811 MB/s | **0.452 MB/s** |
| Relay Alice | 0.599 MB/s | 0.341 MB/s | **0.349 MB/s** |

不能只挑最低的中间 Direct Client 数字作为最终结果：中间运行的 App Update 数量明显较少，机器调度/活跃负载也不同。以下按同步阶段 Count 归一化，更能说明改变发生在哪里：

| 同步阶段 | v3 | 中间 typed | 最终 typed |
| --- | ---: | ---: | ---: |
| Direct Host MotionSend | 24,846 B/region | 23,906 | **5,034** |
| Relay Host MotionSend | 22,977 B/region | 23,924 | **5,229** |
| Direct Alice InputSubmit | 1,397 B/Update region | 104 | **39** |
| Relay Alice InputSubmit | 2,531 B/Update region | 221 | **211** |

InputSubmit region 是一次 App Update 的输入检查，不是一次 RPC；MotionSend 包含测量、发送启动和同步工作，不覆盖后续其他线程的全部操作。中间 Host trace 的 MeasureUnreliableCall 占约 **12.31 / 12.95 MB** 的采样估算；最终 trace 不再检出该主导分配栈。

最终持续业务：Direct Host UDP **9,501** 包，射击/命中 **275/37**；Relay UDP **9,304** 包，**266/57**；基线 UDP 分别 9,358/8,998。Relay 可靠转发最终 **6,637** 条，较 v3 的 11,815 条减少，与取消 void 成功回复的方向相符；该计数包含其他可靠消息，不能等同于精确 ACK 数量。成员、Health、晚加入、死亡复活和退出清理均通过，未通过停流或降低模拟频率制造改善。

## GC 暂停与剩余来源

最终 trace 只计完整 GC suspend/restart，Direct Host **1 次 / 最长 6.16 ms**，Alice **1 / 3.91 ms**；Relay Host **2 / 4.13 ms**，Alice **1 / 7.08 ms**。样本少且环境波动，不能宣布 P99/最长暂停得到稳定改善。

剩余来源明确存在：

最终 Relay Alice 的排他命名空间分组中，Arena.App 约 **7.42 MB**、Arena 约 **2.05 MB**，占该 trace **11.72 MB** 总采样估算的约 **81%**；代表栈是诊断 JSON/文件写入。Runtime 约 **0.97 MB**、会话适配层约 **1.07 MB**。这是最近项目方法的采样归属，不是逐对象精确统计，也不能把 Arena 分组全部等同于可删除的诊断工作。

- App 的磁盘 JSON 报告、文件写入、视图/插值与进程统计。
- 可变长度位姿数组/DTO 解码，以及样例调用测量前的装箱。
- UDP 会话 HMAC/端点等处理，native send operation/TCS，Relay frame wrapper/TCS 及 DMTP 内部。
- 状态同步、显式 reflection proxy/API、Task reply 和真正异步请求的等待机制。

因此目前能确认的是**指定生成型同步 void 路径的零分配**及真实进程的下降，而不是整个网络栈或 Unity 游戏零 GC。动态 Host 协议补表、所有复杂 DTO 池化及完整请求系统重写仍为延期项。

## 暂停、重绑及图形

`20260927-222825-797c26` 为本阶段通过的 native Relay `--pause-udp --rebind-udp --visual`。UDP 暂停时可靠业务继续，恢复及重绑后身份不变且位姿推进，最终 Relay 表清空。生成的 2D/3D 图像由主代理读回确认。该运行在最后一次测量元数据缓存优化前；缓存优化不改变发送格式或游戏语义，最后两次 profile 重新验证了实际通信/清理。

## 复现

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~TypedHotTests --logger "console;verbosity=detailed"
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --transport native
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay --transport native
```

Core 保持 C#9/netstandard2.1，未进行 Unity/IL2CPP、Linux、公网或长时间饱和验证。用法和协议边界见 [typed-rpc-guide.md](typed-rpc-guide.md)。
