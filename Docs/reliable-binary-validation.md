# 可靠二进制迁移验收 — 2026-09-27

此页为二进制 v3 迁移检查点；后续生成式 typed RPC、单向 void 和借用缓冲的最新测量见 [typed-rpc-validation.md](typed-rpc-validation.md)。

## 代码与功能验证

完整 Release solution build：**0 警告、0 错误**。完整测试 **161/161 通过**，0 跳过：Core 55、Transport 13、Datagram 19、Relay 16、Lobby 9、Game 30、App 19。

Runtime 源码和 Core 依赖中已移除 Newtonsoft/JToken；有程序集依赖缺失断言。可靠游戏流量实际使用 binary v3 + MemoryPack，不使用 JSON 回退。测试覆盖实际 woven 的接口/具体实例调用、Host/All/Target、权限与来源、nullable/非 nullable 返回、可变参数冻结、迟加入/迟绑定、状态版本及移除语义、Unicode/嵌套 DTO、malformed/truncated/旧版本报文和嵌套集合长度伪造。

审查补充了混合字段/属性的显式连续 MemoryPackOrder、annotated unmanaged struct 的 raw 格式、nullable int、字符串长度预检和不支持 schema 的编织错误。解码前进行有界预检，不让小包里的巨大声明长度直接触发 MemoryPack 分配。

发现并修复的集成问题：

1. 迁移中曾错误放开 SyncVar 的嵌套集合，原 GameTest 检出。已恢复编织期限制，并加入运行时递归检查；RPC 数组快照测试保留，SyncVar fixture 使用无集合 DTO。
2. 一次并行全套测试在 UDP bind 报 AccessDenied：测试先前只探测 TCP 端口，不能证明同号 UDP 可用。Arena 测试/E2E 改为 UDP/TCP 同号双协议有界探测，只重试端口探测，不掩盖失败的实际测试。释放探测到生产绑定之间的 TOCTOU 仍存在。随后完整 suite 通过。
3. 旧 GameTest 监听 JSON `Kind=call` 的条件已改为实际 binary v3 call header，避免协议迁移后断言空转。

## 真实多进程与分配对照

新旧都使用 **native UDP**，相同 E2E 命令和三 Client bot 场景；前一阶段仍以 JSON 编码可靠游戏消息，新阶段使用 binary v3。新运行顺序执行，各一次，没有失败重跑。

| 场景 | 旧 native + reliable JSON | 新 native + reliable binary |
| --- | --- | --- |
| Direct profile | `20260927-143111-c89c8b` | `20260927-155738-e8ddbf` |
| Relay profile | `20260927-143445-784584` | `20260927-155925-da3ad0` |
| Relay pause/rebind/visual | — | `20260927-160113-4d8ec2` |

目录均位于 `Artifacts/ArenaRuns/`，保留原始 `.nettrace`、解析 `.etlx`、profile JSON、窗口计数器和业务报告。数字为十进制 MB；分配速率由 profile-window 对应节点的全进程计数器差和实际采样时长求得。报告/计数器约有 250 ms/1 s 刷新间隔；EventPipe 是约 35 秒的分配采样估算，两者不是同一精确计量。

| 路线／节点 | 旧 JSON：MB/s | 新 binary：MB/s | 旧 / 新 EventPipe 估算分配 |
| --- | ---: | ---: | ---: |
| Direct Host | 2.032 | **1.051** | 71.95 / 36.27 MB |
| Direct Alice | 0.879 | **0.501** | 30.61 / 17.10 MB |
| Relay Host | 2.383 | **1.279** | 83.49 / 44.42 MB |
| Relay Alice | 1.027 | **0.599** | 35.60 / 20.58 MB |

新采样中没有发现原来的 Newtonsoft/JToken 分配堆栈，实际可见 `ReliableCodec.Encode/Decode`。例如 Direct Alice 中两者仍分别包含约 **1.19 MB / 1.40 MB** 估算分配，不能称为零分配，也不能把包含调用栈重复相加。`System.Text.Json` 仍出现在 App **磁盘报告**调用栈，这不是游戏 RPC 回退成 JSON。

业务量核对：Direct Host 新运行 **9,358 UDP 包、271 射击/51 命中**，旧为 9,370、272/50；Relay 新为 **8,998 转发包、240/91**，旧为 9,276、266/46。Relay 的命中与生命周期频率明显不同，不能把所有差额精确归因于编码器，也不能宣称严格确定性 A/B。

## GC 和剩余开销

新 trace 的 GC 暂停（只算完整 GC suspend/restart，不含 SuspendOther）：

| 节点 | 次数 | 最长 |
| --- | ---: | ---: |
| Direct Host | 3 | 3.09 ms |
| Direct Alice | 2 | 4.35 ms |
| Relay Host | 3 | 4.79 ms |
| Relay Alice | 2 | 5.15 ms |

分配减少不等于最长暂停同步减少。采样短、次数少，未建立稳定 P99 或 Unity 帧时间指标。

隔离的 warmed fake-wire `Add + SyncVar + reply` 仍约 **7,386 B/远端调用、5,547 B/Host 本地调用**，包括回复/状态、反射与调用调度。此项只用于揭示残余成本，不和全进程 MB/s 混用。暂存 writer 池化避免每个标量一次的默认 256 B scratch，但值/帧的独立拥有数组仍分配。

**当前 0.5–0.6 MB/s 的 Client 全进程分配仍不作为 Unity 低 GC 验收通过。** 后续应分别约束高频网络本身与 Sample 显示/报告成本，继续处理参数/帧数组、等待器/超时、装箱、方法元数据及位姿重复测量，而不是认为二进制化已经解决整个问题。

## 通道和图形读回

`20260927-160113-4d8ec2`：暂停 UDP 时 PositionTick 固定 **58**、接收包数 **37**，可靠探测 **1→5**、Health 更新 **4→7**；恢复后 Tick **156**。实际重绑后身份保持，Tick 从完成标记附近 **496→501**，替换 Socket 已收到 8 包。最大实际 UDP 包 **550 B**。最终 Relay rooms/clients/admissions/UDP bound peers 全为 0。

Alice 2D / Bob 3D PNG 已读回，显示真实 Relay ready、成员/HP 与 UDP bound 状态。完整晚加入、移动、伤害、复活和退出断言通过。没有用关闭可靠业务来制造分配降低。

## 复现

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --transport native
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay --transport native
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --relay --pause-udp --rebind-udp --visual --transport native
```

可靠协议 v3 要求参与端同步升级；DTO 声明与限制见 [迁移指南](reliable-binary-guide.md)。未进行 Unity、IL2CPP、Linux、公网或长时高人数验证。本库尚无提交版本号，证据以本地日期目录定位。
