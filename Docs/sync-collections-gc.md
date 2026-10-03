# 同步集合 GC 定位与优化 — 2026-09-28

后续状态：优化已合入主库，Unity 接入也已完成；以下数字仍是 .NET sandbox 的分配计量，不是 Unity Mono 数据。见 [Unity 接入记录](unity-sync-collections.md)。

## 结论

首版只完成了功能验收，分配成本确实偏高。256 项 Dictionary 改一个 int，真实 Direct TCP/DMTP 两端合计原来约 **8,208 B/次**。本次优化后约 **944 B/次**，下降 **88.5%**，仍不是零 GC。

范围是 `Net.BITKit.Multiplayer.Sync` worktree；没有修改 live 库、GC sibling 或 Unity。未改变线上字节格式、Host 权威、回调 API 或集合副本/原子性契约。

## 基准方法

`Tests/SyncAllocationTests.cs` 为 opt-in fixture，使用实际编织的 `SyncPerfState`。不使用记录每次变化的 List、日志输出或测试 wire 的消息历史，以免把诊断增长当成库成本。

- Windows `10.0.22631`，**.NET 10.0.11**，Release。
- 每路径预热 128 次，随后三批各 500 次调用。
- 计数器 `GC.GetTotalAllocatedBytes(true)`，同进程 Host + 一个 Client 合计，包含固定测试 harness；不强制 GC。
- 单独区分同步测试 wire 与真实 TCP/DMTP。每次修改核对接收版本，结束核对内容和错误数，未减少投递工作量。
- 调用方复用批次 Edit 数组和输入 DTO；构造、绑定、快照、最终检查与写报告不在计量窗口内。
- 批次每次修改 8 项；HashSet 每次调用执行 Remove + Add 两次独立修改。
- DTO 是一个 `int Count + string Name` 的小型 MemoryPack 对象，不代表任意复杂 DTO 的成本。

原始证据：本 worktree `Artifacts/SyncPerformance/baseline.json` 与 `optimized.json`。下表为三批中位数。

## 前后对照

### 真实 Direct TCP/DMTP

| 场景 | 原来 B/调用 | 优化后 B/调用 | 优化后 B/元素修改 |
| --- | ---: | ---: | ---: |
| 256 项 Dictionary，修改一个 int | 8,208 | **944** | 944 |
| 256 项 Dictionary，一批改 8 项 | 19,368 | **2,776** | **347** |
| 64 项 Dictionary，替换一个小 DTO | 5,665.06 | **1,240** | 1,240 |

优化后的三批 TCP 单项 int 都为 944 B；批次为 2785.216 / 2776 / 2776 B；DTO 为 1240.656 / 1240 / 1240 B。没有隐藏首批波动。

### 同步测试 wire（无 socket）

| 场景 | 原来 B/调用 | 优化后 B/调用 |
| --- | ---: | ---: |
| Dictionary 1 项，改单项 | 2,152 | **832** |
| Dictionary 64 项，改单项 | 3,512 | **832** |
| Dictionary 256 项，改单项 | 7,952 | **832** |
| Dictionary 256 项，批量改 8 项 | 19,000 | **2,664**（333 B/项） |
| Dictionary 相同 int 赋值 | 40 | **0** |
| List 256 项，改单项 int | 2,952 | **824** |
| HashSet 256 项，Remove + Add | 17,376 | **1,616**（808 B/修改） |
| Dictionary 64 项，替换小 DTO | 5,400 | **1,128** |

关键不只是下降百分比：单项修改分配已经不再随 Dictionary 从 1 项增长至 256 项而增长。零分配断言只覆盖预热后的 int 无变化赋值，不是有网络更新时的零分配承诺。

## 实际去掉的热点

1. **每个值反复生成 byte[]**：尺寸计算、编码、DTO 比较/复制原来调用 Encode→CopyOwned。现在固定无引用值用已知固定尺寸；编码直接写 pooled scratch，通过回填长度前缀消除中间数组；DTO 从借用 Span 比较/解码。
2. **增量组包多次复制**：原来 operation bytes → body bytes → 带头 frame。现在头、操作数、操作内容写入同一个 scratch，仅产出一份独立拥有的队列 payload。
3. **每个 Client 增量复制整个容器**：单操作现在先完整解码、语义/长度/尾部校验、准备通知，再原地提交。多操作和快照仍 staging，但每个容器复用一个有界备用 store，提交/失败后清空引用并回收。
4. **每包收件人数组和 Packet.Copy**：复用冷维护的成员快照，顺序 writer 不再为每个 peer 复制 Packet。
5. **接收可靠帧整包 ToArray**：直接同步解析 borrowed memory，只有确实需要独立拥有的 blob 继续复制。
6. **scratch writer 对象**：使用每线程最多 8 个对象的有界缓存；每次 Dispose 仍归还其 ArrayPool 缓冲。嵌套租约互不覆盖，最终 owned 输出不借用 scratch。

`ToDictionary` 审计：本次新增的调用在测试构造指纹的辅助代码，`Src` 运行时未找到 `ToDictionary`。实际热路径问题是上面的数组、ToArray 与整容器复制；不能仅按 API 名称判断冷热。

## 保留的成本与取舍

- 可靠信封仍使用 Packet 对象、路由字符串及独立拥有的编码/解码字节数组；新队列 payload 也必须活到真实发送完成。
- DTO 的所有权复制、字符串和业务对象解码继续分配。
- 批次的操作记录/输入转换仍有分配；备用容器减少 GC，但批次 staging 的 O(n) 复制 CPU 成本仍存在。
- 当前 spare store 只供内部事务使用；枚举仍要求业务串行化，不能把它当成可跨后续修改长期持有的快照。
- 墙钟样本有波动，本轮不宣称吞吐、延迟或 GC 暂停与分配同比改善。

按本次条件估算：每秒 20 次真实单项 int 更新约 **18.9 KB/s**，而不是原来的 164.2 KB/s；每秒 20 个八项批次约 **55.5 KB/s**，对应每秒 160 项修改。这些都是一 Host/一 Client 整条更新路径合计，不是完整 Arena 进程，也不是每个节点各自的数值。

## 验证

- Core **93/93** 通过；opt-in allocation fixture 常规运行跳过，另行完整通过。
- Arena.GameTests **30/30** 通过；netstandard2.1 构建零警告、零错误。
- 新增：预热后 2,000 次 int 无变化赋值 0 B 硬断言；单操作附带非法尾部不得提交；反复失败后备用 staging store 可恢复；嵌套 scratch 租约和 owned 输出隔离。
- 原有批次恶意第二 opcode、DTO 别名、发送积压/解绑、真实 Direct/Relay、指纹和版本测试继续通过。
- 未做本功能的 Unity、IL2CPP、Linux 或 Arena 全进程 GC profile。

复现（在本 worktree 根目录，单独进程运行）：

```powershell
$env:BITKIT_SYNC_BENCH_OUTPUT = "$PWD\Artifacts\SyncPerformance\repeat.json"
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --filter FullyQualifiedName~SyncAllocationTests --nologo
$env:BITKIT_SYNC_BENCH_OUTPUT = $null
```
