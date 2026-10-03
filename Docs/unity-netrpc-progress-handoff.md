# Unity 新 NetRpc 接入交接（2026-10-03）

目标：主 Agent 接 Unity；另一个主 Agent 在独立 worktree 优化 Runtime/Transport 性能。本工作线修改 Unity adapter / codegen 接口与 probe，不继续重写 Runtime 性能路径。

**后续性能工作线已整合主目录**：默认 UniTask receiver/transport/Session/业务 probe、ILPP marker、原生生成器均保留并打通，合入后的 smoke、adapter/lifecycle 重新通过。最新交接见 [主目录整合记录](netrpc-main-integration.md)；下方最初 Task/ValueTask 形状是历史检查点。`Session.Disposal` / `OutboundCompletion` 现在是可复用的 preserved UniTask，Task 只留在 Editor smoke polling/有界测试等互操作入口。业务跨异步边界显式切回主线程。

## 当前可直接使用

实际宿主 `Com.Project.B.Unity`，Unity **2022.3.62f3**，`map_prototype`，非 Play。已读取 Project B overlay、project-local codebase/Funplay skills；按 codebase policy 全部使用 Edit Mode，没有改场景。**原生 Funplay MCP 已可用**；HTTP fallback 为 8769。

**新 backend 的编译、实际 ILPP / 原生 DI 代理、公开 Unity Session、真实 loopback TCP+UDP、业务/Changed/await 主线程、权限、窗口/初始化关闭与 Ready 状态域重载已通过。** 详细证据及公开 API/内存/队列限制见 [Unity 新 NetRpc 验收](unity-netrpc-validation.md)。

菜单：`Tools/BITKit/NetRpc/Open Unity Sync Lab`。这个窗口使用新 MessagePack backend，不是旧 `Tools/BITKit/Multiplayer/Host`、B6 或 TouchSocket。

## 文件地图

- `Src/Contracts/RpcContracts.cs`：assembly `NetRpcBackend` opt-in、接口 `NetRpcRemoteContract` 输出路径。
- `Src/Editor/CodeGen/TypedRpcILPostProcessor.cs`：显式选 `WeaveNetRpcModule`，未 opt-in 的 assembly 保留旧 backend。
- `Src/Editor/CodeGen/NetRpcWeaver.cs`：成功后添加 `WovenAssembly` marker；与 CLI 共享，Unity PE/PDB 使用现有安全写出。
- `Src/Editor/NetRpcGeneration`：独立 Editor assembly，reload/菜单生成原生源码，仅可写 Assets/Local/Embedded package；已有普通源码不会被覆盖。
- `Src/Unity/NetRpc/UnityNetRpcDispatcher.cs`：有界 ingress/control、拥有副本的 transport、串行 outbound worker；32 transport 的保留 control slot，关闭/late Closed 不丢失；停止后不执行业务。
- `Src/Unity/NetRpc/UnityNetRpcSession.cs`：公开 DI/Runtime composition owner，主线程 Pump 发布；`Disposal` 等待可选连接 owner 和 outbound worker。
- `Src/Unity/NetRpcProbes/UnityArena.cs`：纯 C# World/Service/Commands、UniTask Attack、Task await、生命周期取消、unreliable All、ECS/接口状态。`Generated/UnityArenaRemote.g.cs` 已实际生成/导入。
- `Src/Editor/NetRpcProbe/UnityNetRpcLab.cs`：可见 EditorWindow、两个实际 Session / socket owner、异步 smoke/lifecycle、reload 的 SessionState 读回。

## 最新实际读回

MCP 项目路径正确，编译/reload recovery 成功，compilation errors 为空。真实代理 `NetRemote_3844293574`，opt-in/编织 marker 已读回。

```text
PASS Unity 2022.3.62f3: proxy=NetRemote_3844293574; woven=True;
Plus=42; HP=80; X=128; List=1; Dictionary[1]=1; All=1/1;
async-main-thread=true; Changed-main-thread=true;
authority-denied=true; notifications=6
Status=RanToCompletion; Active=False
```

Adapter：借用数组 poison、item/byte 上限、32 transport 同时 Closed/late Closed、拒绝第 33 个、in-flight 取消/排队数组归还、wire/dispatcher disposed 回调抑制，`pending=0; bytes=0`。

Lifecycle：关闭真实窗口，取消 lifetime-aware 已进入业务，pending request 结束，两端 TCP/UDP/proof workers 完成，tick/Changed 冻结，重新创建后 smoke 通过；初始化期间立即 Stop 也完成。

Ready lab 实际 script recompilation/domain reload 后：

```text
ready=True; disposal-complete=True; pending=0; bytes=0; active=False
Active-after-reload=False
```

重载后新会话 smoke 通过。窗口已实际截图；最近 cached console error 为空。独立库 `NetRpcWeaverTests` **4 通过 / 0 失败 / 0 跳过**，不是新的全量测试数字。

## 下一次行动

1. 按宿主 AGENTS/overlay/skills 确认原生 MCP 指向正确 Project B 与 Edit Mode；不要重复跑整个历史 suite。
2. Session 成为新 Unity 业务接入口：构造/Attach/Pump/Dispose 主线程，退出 await `Disposal`；业务 await 自行观察 world/session token。连接 owner 不交给 Session 时调用方负责释放。
3. 根据用户下一目标接场景/世界租约、身份权限或独立 Unity 进程/Player/AOT。当前只验证同 Editor 双 Runtime；完整游戏、场景迁移、Unity Relay/LiteNetLib、公网与 Unity GC 未完成。尚未 Ready 的域重载未单独验收。
4. 如改 adapter，`AdapterLifecycleReadback()` 做同步 fake 检查；`BeginSmokeReadback()` / `BeginLifecycleReadback()` 启动真实异步验证，再短轮询 `LastSmoke` / `LastLifecycle`。**不要在 Editor 主线程同步等真实 smoke Task。** 必要时用 AppDomain assembly reflection 调用，避免 snippet 引用解析问题。
5. 如改源码，request recompile 后读 recovery；中断/取消不是成功。Ready reload probe 的有界同步等仅依赖 socket/send worker，不可套用到等待 Unity continuation 的初始化或业务 Task。

新 generation/lab/runtime-adapter 各有独立 asmdef；legacy `GameRpcSession` 与游戏业务本轮没有替换。Lab 的 static Active 只是测试窗口生命周期，不是业务的全局 Runtime。无有效 HEAD、源码大量未追踪，不能用空 diff 当作没有改动；没有 commit/push。
