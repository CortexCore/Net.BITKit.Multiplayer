# NetRpc 热点优化 / 默认 UniTask：主工作树整合

2026-10-03。用户确认可以合入主分支后，将 `Net.BITKit.Multiplayer.Hotspots` / `perf/netrpc-hotspots` 的优化逐文件整合到 `D:\Iris\Documents\GitHub\Net.BITKit.Multiplayer` 的 **master 工作树**。

仓库仍没有有效 HEAD，源码未跟踪；本次完成源码整合，没有初始提交、merge commit、push 或发布。优化 worktree 仍保留。

## 三方审查与保护

- 原始基线：优化 worktree 的 `Artifacts/HotspotsBaseline/`。
- 本次主目录整合前快照与 SHA256 plan：`Artifacts/AgentBaselines/netrpc-hotspots-main-integration/`。
- 核对 51 个明确目标文件：44 项主目录未并行修改 / 新文件直接整合；7 项（Weaver 和 6 个文档）人工合并，保留主目录的 Unity 接入内容。
- 未整目录复制 Src，不覆写 Contracts 的 assembly opt-in、ILPP 选择、Unity 生成器、Session/Dispatcher/Probe 和旧 backend 的并行修改；源文件原编码与换行在批量整合时保留。
- Unity 现场为正确 Project B / 2022.3.62f3 / Edit Mode，整合前 lab inactive。批量改动期间临时暂停 AutoRefresh，随后已恢复并完成实际域重载与 recompilation。没有进入 Play、没有改场景，也没有切换宿主包路径。

## 本次冲突适配

1. `NetRpcWeaver` 保留主目录 `WovenAssembly` marker，合入 FinishUniTask / CompleteUniTask 的直接链路，未恢复 Task 中转。
2. `UnityNetRpcTransport.Send/SendFast` 返回 UniTask，仍同步复制并排队，借用提交语义与 item/byte 上限保留。outbound worker 改 UniTask + ThreadPool，completion 使用 Preserve 支持生命周期重复读回。
3. `UnityNetRpcSession.Disposal` 为 preserved UniTask，关闭连接、出站 worker 使用 UniTask WhenAll；标准 IAsyncDisposable 是明确的冷路径互操作。
4. Unity 业务接口 / probe 默认 UniTask；异步业务和 socket admission 后明确 SwitchToMainThread，保留 session lifetime 取消及线程断言。
5. 原生 Unity proxy 更新为 UniTask receiver；新 Plus/Pickup method ID 为 4123136618 / 2474660799，编译后实际生成器继续负责原生源码。两端同时更新返回类型和编织输出。
6. TCP receive/UDP/proof 的长寿命 UniTask 用 Preserve，支持停止后 worker 状态读回；池化操作不通过已消费的陈旧 token 查询。
7. Editor 的 LastSmoke / LastLifecycle 与有界 synchronous fake 检查保留 Task 互操作表面；真实网络 smoke/lifecycle 只异步启动后轮询，未在 Editor 主线程阻塞等待。

默认栈 / 跨 await call context / API 升级要求继续见 [UniTask 契约与性能证据](netrpc-unitask-default.md)。

## 主目录 .NET 验证

- `dotnet build Net.BITKit.Multiplayer.slnx -c Release -m:1`：通过；0 error，原有 unused event warning。
- 完整 suite：**295 通过 / 0 失败 / 2 原有 benchmark 跳过**。
- 强制加载 netstandard2.1 Transport 的真实 socket 兼容：**4/4**。
- 主目录构建后的 Native Direct / Relay Sample：均 PASS（Plus=42、Health=85、Items/Counts、All 行为）。
- TRX：`Artifacts/TestResults/HotspotsIntegration/`。

主目录独立真实 Host/Client（Relay 含第三个 Relay 进程）确认性能未丢失：

| 负载 | 测量次数 | 全线程精确合计 B | B/op | 行为 |
| --- | ---: | ---: | ---: | --- |
| Direct scalar int RPC | 30000 | 2882936 | 96.098 | 结果与 request/return 帧计数正确 |
| Relay scalar int RPC | 30000 | 2881920 | 96.064 | 结果与两跳路径正确 |
| Direct Component | 20000 | 6672 | 0.334 | 20000 回调，最终值/版本正确 |
| Direct 256 项 Dictionary Set | 20000 | 8856 | 0.443 | 20000 回调，最终值正确 |

这是合入确认单批，不替代优化 worktree 的三轮中位数；含固定控制/排空开销，不扣 idle、不外推 Unity GC。主目录证据：`Artifacts/NetRpcPerformance/main-integration-{scalar,component,dictionary}/`。

## 合入后 Unity 实际读回

Project B **Unity 2022.3.62f3**，当前挂载主目录 `Src`，UniTask UPM **2.5.10**。

- 首次 source integration 引发真实域重载；MCP recovery 记录中断后恢复，不将中断当成成功。再次 `wait_for_compilation(force_refresh=true)` 返回 **Compilation complete (7.0s). No errors detected.**
- 当前加载 `NetRpcReceiver.Invoke` 返回 **UniTask<NetMessageBag>**，`ITransport.Send` 返回 **UniTask**，证明不是沿用旧 Core。
- `BeginSmokeReadback` → `LastSmoke`：**RanToCompletion、Active=False**：

```text
PASS Unity 2022.3.62f3: proxy=NetRemote_3844293574; woven=True;
Plus=42; HP=80; X=128; List=1; Dictionary[1]=1; All=1/1;
async-main-thread=true; Changed-main-thread=true;
authority-denied=true; notifications=6
```

- `AdapterLifecycleReadback`：借用 ingress/outbound 数组 poison 后仍正确、主线程派发、item/byte 上限、overflow Closed 一次、32 个预留关闭控制槽与 late subscribers、in-flight send 取消、排队数组归还、disposed 回调抑制，**pending=0; bytes=0**。
- `BeginLifecycleReadback` → `LastLifecycle`：**RanToCompletion、Active=False**：窗口关闭取消已进入业务、pending 请求结束、TCP/UDP/proof worker 完成、queue=0、tick/Changed 冻结、重建新会话 smoke 通过、初始化阶段 Stop 通过。
- 原生 Funplay 后续读 editor state：**非 Play / 非编译 / 非更新**。
- 最终原生 Funplay 再次核对 package resolvedPath 为主目录 `Net.BITKit.Multiplayer/Src`，Plus 返回 UniTask<int> 且 method ID=4123136618；LastSmoke / LastLifecycle 均 RanToCompletion、lab inactive，当前编译错误为空、最近 600s cached console error 为空。

本轮重新验证的是编译与最小 Edit Mode 原生 sockets/主线程/退出，不是全游戏迁移。最初 Unity 接入记录和 Ready reload 历史证据仍保留在 [原验收](unity-netrpc-validation.md)。没有为这次合入重复执行 Ready-window reload 或启动独立 Unity Player。

## 后续与工具边界

可以从主目录 `UnityNetRpcSession` 继续接业务。尚未完成：独立 Unity 进程、Player/IL2CPP/裁剪、完整游戏/场景生命周期、新 backend Unity Relay/LiteNetLib、公网、高人数长压及 **Unity Mono GC/帧时间**。

用户新增的原生 Funplay 已可正常调用；当前 .NET MCP 的 Roslyn workspace_load 对此仓库返回 sanctioned-root boundary 拒绝。此限制未阻塞本轮真实 MSBuild/test 验证，没有擅自修改 MCP 配置。扩大工具允许根目录属于后续工具配置工作。

不要因为 `git diff` 为空误判没有修改；没有 HEAD 时审核使用整合 plan 与冻结 sources，并保留 master 工作树的新 Unity 文件。需要 Git 提交时另行建立明确基线和提交范围，不把整个未跟踪仓库或本机 Artifacts 自动提交。
