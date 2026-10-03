# NetworkTime 与 Editor 时间 Label

`RpcRuntime.NetworkTime` 是每个房间自己的网络时钟，使用 double 秒数和 Stopwatch，不依赖 Unity `Time.timeScale`。没有全局 current Runtime。

```csharp
double seconds = session.Runtime.NetworkTime.time; // Time 为同义 PascalCase 属性
bool synchronized = session.Runtime.NetworkTime.IsSynchronized;
double rttSeconds = session.Runtime.NetworkTime.RoundTripTime;
```

- Host：从本 Runtime 创建起的单调秒数；Offline 也使用本地单调时间。
- Client：首次成功校时前 IsSynchronized=false、time=0；通过已认证可靠通道估算 Host 时间，补偿 RTT/2，然后本地推进。
- Client ready 后自动校时，定时器间隔2秒；最多一个未结束发送和一个待响应样本，样本5秒超时，旧序号/非法时间被忽略。Host 对每 peer 限频且最多一个活动回复，总回复并发不超过成员上限。
- 选择低 RTT 样本，每15秒允许更新选择窗口。校正后读数不倒退，但可能短暂停住等本地时间追上。
- Host 离场或 Runtime Dispose 后时钟冻结，IsSynchronized=false。业务同时检查会话 IsReady。
- 这是基础 RTT 对时估算，不是精确 UTC/PTP；不对称网络与主线程排队会影响精度。不是昼夜日历或游戏倍率系统。
- 可靠信封新增 timeRequest/timeReply kind 10/11；双方应使用同版 Core。校时是低频控制流，有少量分配；之前集合 GC 报告不包含本次新增的周期校时预算。

## 简单可见演示

打开 **Tools > BITKit > Multiplayer > Host / Client**。

Host 从12:00:00开始，每秒将 NetworkTime 的整数秒写入 `RpcProbeService.ClockSeconds` SyncVar。两窗的 **Time (SyncVar)** Label 都读取自己实例的状态；Client 不从 Host 窗口取值，也不本地假走时。Hook 只更新线程安全的显示值，OnGUI 在主线程读取；文本仅秒数变化时重新格式化。

MCP 可调用 `EditorProbeDiagnostics.StartSmoke()`，测试后窗口保留运行。`GetStatus()` 增加 HostClock、ClientClock、ClientClockUpdates、TimeSynchronized 和两端 NetworkTime 值。

Unity 2022.3.41f1 Edit Mode 已读回 HostClock=ClientClock=43220、ClientClockUpdates=17、TimeSynchronized=true；同次读取的 Host/Client NetworkTime 约26.41323/26.41347秒。截图中两个 Label 均为12:01:02。误差只是这次 loopback 样本，不是精度承诺；没有改全局 Time.timeScale。

测试：`Tests/NetworkTimeTests.cs` 覆盖单调/冻结、RTT补偿、旧/非法样本、单待响应与超时、实际 Runtime admission 后校时和 Host 离场。
