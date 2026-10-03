# 新 NetRpc 集合 GC 收敛与限时验证

2026-10-03。协调 Agent 直接修改主目录，LiteNetLib 与基线工具分别来自经校验的隔离 worktree；没有创建提交。

## 已交付

- LiteNetLib Direct 已整合主目录；接收派发/关闭/早到包/资源退出竞态已修复，15 项适配器回归通过。
- `Tools/NetRpcPerformance` 已整合：真实独立 Host/Client/Relay 进程、所有线程 GC 计量、吞吐/延迟/回调与 UDP 发送/接收/应用分别统计。
- 主目录 26 个 native Direct/Relay 基线全部通过，证据：`Artifacts/NetRpcPerformance/integrated-native-baseline/`。这是优化前记录。
- 本轮优化 `Src/Runtime/NetworkCollections.cs`：单条增量先完整解码/Complete/合法性预校验，再原位修改，不再为每次 Set 复制全容器；完整快照仍在独立 scratch 容器解码，成功后交换，scratch 可复用；CopyTo 不再构造中间 List。

## 原子性和语义

非法索引、重复 Add、缺失 Remove、未知 opcode、截断或尾随参数都在 live mutation 之前失败。失败不推进 revision、不触发 Changed。完整快照错误不交换当前存储；下一次有效快照继续可用。Hooks 仍在提交后、锁外触发。

新 `NetworkCollectionHotTests` 4/4：列表/字典非法增量、scratch 快照失败后恢复、CopyTo 预热 0 B。所有 Core 回归 139 通过 / 1 项原有性能测量跳过。

## 相同实际 TCP 负载对照

每项 100 warmup + 500 次有变化的 Set，固定容器逻辑项数，真实独立 Host/Client，所有线程 GC.GetTotalAllocatedBytes(true)，不做 idle subtraction。每项最终状态正确且收到完整 500 回调。

| 容器/项数 | 优化前 Host+Client B | 优化后 B | 变化 |
| --- | ---: | ---: | --- |
| Dictionary / 1 | 333560 | 193224 | -42.1% |
| Dictionary / 128 | 1591784 | 228304 | -85.7% |
| Dictionary / 256 | 3134968 | 189224 | **-94.0%** |
| List / 1 | 220024 | 211288 | -4.0% |
| List / 128 | 481080 | 170472 | -64.6% |
| List / 256 | 759016 | 178272 | **-76.5%** |

优化后 Dictionary 256 项约 378 B/更新，List 256 项约 357 B/更新（Host+Client 原始合计包含控制/排空开销）。不再有随容器大小近似线性增长的每次 Set 数组/哈希表复制。短跑数字受调度/线程池影响，不代表完全 0 GC 或长期预算。

证据：`Artifacts/NetRpcPerformance/collection-delta-after/`、`list-delta-after/`；raw role JSON/aggregate 与 begin/end stamps 保留。

```powershell
dotnet build Tools/NetRpcPerformance/NetRpcPerformance.csproj -c Release -m:1
dotnet Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll --transport direct --profile dictionary --sizes 1,128,256 --iterations 500 --warmup 100 --timeout-seconds 15
```

## 当前主目录验收

分项目运行并设置测试挂起超时，全部完成：Core 139、LiteNetLib 15、GC guards 12、native Transport 16、Datagram 22、Relay 20、Arena App 19、Game 30、Lobby 9；**合计 282 通过，0 失败，2 项原有可选性能测量跳过**。

实际两 Godot 进程在主目录通过：

- LiteNetLib Direct：`Artifacts/NetRpcGodot/20261003-112320-litenetlib-direct/summary.json`。
- Native TCP+UDP Relay：`Artifacts/NetRpcGodot/20261003-112816-relay/summary.json`。

当前 Unity MCP 未连通，因此不把此次源码修改写成新的 Unity 编译/运行证据。

## 长测试和进程清理

此前完整 slnx 串行测试外层 120s 超时，取消 Worker 留下了一个 LL benchmark Host（已核对命令行并停止 PID26080）。此后不用无限等待：分项目外层 35～75s，`--blame-hang-timeout 20s/25s/30s`；benchmark 子进程控制读取/退出也有明确超时。

`Child.DisposeAsync` 的 quit 写入、flush、等待退出现在全部限时，失败后仅结束自己持有的子进程树。没有结束用户的普通 PowerShell 窗口。

LiteNetLib 与 native 使用共同 GC workload 的对照扩展在隔离目录中被取消，**尚未完成/整合**，不能把隔离目录中的部分结果当成已验证的比较工具。当前稳定基线支持 native Direct/Relay；LL 游戏/适配器验收已完成。后续工作是 LL 同负载 GC 比较、回复所有权/标量缓存/UDP 接收，以及引擎 UI 单独计量。
