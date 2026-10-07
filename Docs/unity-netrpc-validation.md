# Unity 新 NetRpc：接入与 Edit Mode 验收

> 日期化历史证据：本文的后端选择标记、Session 名称和主库 Src/Unity 路径已更新。当前入口见 [Unity 接入](unity-integration-plan.md)，本轮验证见 [清理证据](legacy-backend-removal.md)。

验证日期：2026-10-03。宿主 `Com.Project.B.Unity`，实际 Unity **2022.3.62f3**，`map_prototype`，全部在 **Edit Mode**。通过原生 Funplay MCP 确认项目路径、编译、实际程序集及网络行为；没有进入 Play 或修改场景。

这是 `BITKit.Multiplayer.NetRpc` 的 MessagePack / native TCP+UDP 路径，与历史 B6 / TouchSocket 的 Host/Client 窗口分开。

**默认 UniTask 整合更新**：性能 worktree 已合入主工作树，并在同一宿主重新验证编译、原生 UniTask proxy/receiver/transport、smoke 与 adapter/lifecycle；见 [整合读回](netrpc-main-integration.md)。下方初次 Task/ValueTask 接线是历史证据；当前 `Session.Disposal` / `OutboundCompletion` 为 preserved UniTask，业务 UniTask 不自动捕获 Unity synchronization context，需明确主线程切回。

## 入口与程序集

菜单：**Tools > BITKit > NetRpc > Open Unity Sync Lab**。

窗口的 Start 在同一 Editor 中创建一个 Host 和一个 Client；它们拥有独立 DI、Runtime、dispatcher，并通过真实 loopback TCP+UDP 连接。Move、Attack、Pickup、UDP All 操作同步两个实体、生命值与集合；Run Smoke 会自动检查结果并关闭会话。

| 层 | 源码入口 | 职责 |
| --- | --- | --- |
| ILPP | `Src/Editor/CodeGen/TypedRpcILPostProcessor.cs`、`NetRpcWeaver.cs` | 实际 Unity 编译期编织；新旧 backend 显式选择 |
| 原生代理生成 | `Src/Editor/NetRpcGeneration/UnityRemoteInterfaceGenerator.cs` | reload / Generate Remote Interfaces 菜单生成并导入 C# |
| Unity 会话 | `Src/Unity/NetRpc/UnityNetRpcSession.cs` | DI、Runtime、主线程 Pump、连接及出站退出的 ownership |
| 调度与 wire | `Src/Unity/NetRpc/UnityNetRpcDispatcher.cs` | 有界 ingress / control / outbound，池化借用内存 |
| 纯 C# 世界 | `Src/Unity/NetRpcProbes/UnityArena.cs` | 业务主线程断言、Task/UniTask、ECS 与接口状态 |
| 可见窗口与验收 | `Src/Editor/NetRpcProbe/UnityNetRpcLab.cs` | 真实 sockets、异步 smoke、窗口关闭/重启/reload 检查 |

### 显式选择新 backend

在业务程序集加入：

```csharp
[assembly: BITKit.Multiplayer.NetRpcBackend]
```

在需要原生代理的接口声明输出路径：

```csharp
[NetRpcRemoteContract("Assets/Game/Generated/ArenaRemote.g.cs")]
public interface IArena
{
    UniTask<int> Plus(int a, int b);
}
```

输出只能位于可写的 `Assets/` 或 Local/Embedded UPM package，不能写入 Registry/Git 缓存。生成器只替换带自身 auto-generated 标识的源码，拒绝覆盖普通已有源文件。代理源码真实编译到业务 assembly；Unity 不使用 Roslyn JIT remote adapter。没有 assembly opt-in 的旧业务保留历史编织路径。

## 公开 Session 的调用契约

使用 `UnityNetRpcSession(IServiceCollection, bool host, ulong scope)` 创建会话。先注册业务对象/接口（`AddNetRpcObject`、Host 的 `AddNetRpcService`、Client 的 `AddRemoteInterface`），允许对应契约，并在 Host 解析服务，使它成为发布对象。完整可运行接线见 Lab 的 `CreateSession`、`RegisterEntities`、`Initialize`。

- 构造、`AttachPeer`、`Pump`、`Dispose` 在 Unity 主线程进行。每个 Session 拥有独立 Runtime，没有全局 current-runtime。
- `AttachPeer(peer, transport, lifetime, connectionOwner)` 包装底层 wire；可选的 `IAsyncDisposable connectionOwner` 由 Session 退出时释放。未转交 owner 的底层连接由调用方负责。
- 每帧 `Pump(unscaledSeconds)`：主线程执行接收/control；Host 每 50ms 发布变化，每 1s 强制快照。没有开启 Core 的后台状态发布循环。
- `Dispose()` 立即取消 session lifetime、停止回调和归还排队资源；随后 **await `Disposal`**，确认连接 worker 与已开始的出站发送都退出。关闭失败会体现在 `Disposal` 的异常中。
- 已经进入业务的异步方法必须自行观察会话/世界 lifetime，并在 await 后访问 Unity 对象前检查。Session 不能自动回滚业务副作用；验收中的 `LifetimeThread` 使用世界 lifetime 取消延迟后续。
- UniTask 业务 await 不自动捕获 Unity synchronization context；跨网络/线程池等待后访问 Unity 对象必须通过 dispatcher 或显式 `UniTask.SwitchToMainThread`。需要跨 await 的业务 sender/target 在入口保存不可变 Current。

### 有界队列及内存

- ingress：最多 256 个排队数据包、4 MiB；默认每 Pump 最多处理 128 个数据/control 项。
- control：每个 dispatcher 最多注册 **32 个 transport**，每个 transport 预留一个合并控制项。第 33 个注册直接拒绝；32 个同时断线不会挤掉 Closed，late Closed 订阅也合并后在主线程投递。会话释放前仍注册的断线 transport 计入此上限。
- outbound：最多 128 个排队包（另可有一个正在发送）、合计 4 MiB，包括 in-flight 字节。worker 串行交给底层 Send/SendFast。
- 借用 ingress/outbound bytes 在源回调/Send 返回前复制为拥有的池化数组；真正消费/底层异步发送结束后归还。Dispose 取消 in-flight 发送并立即归还尚未发送的包。
- wrapper Send 的完成表示 **借用内存已经复制、排队提交完成**，不表示底层 socket 写完或远端 ACK。RPC 的业务结果仍由 Runtime 请求/回复等待。同步排队提交避免状态发布内部的异步续执行把后续属性读取带离 Unity 主线程。
- 池化与有界队列不等于零 GC；本轮没有测 Unity Mono 分配。

## 实际证据

### 编译、生成、ILPP

MCP 确认 `Application.dataPath` 为 Project B。源码导入引发实际域重载后 recovery 报告脚本编译成功；当前 compilation errors 为空。

业务 assembly 含 `NetRpcBackendAttribute`、`WovenAssemblyAttribute`；最终实际读回 **5 个 `__netrpc_recv_*`**，对应 Move、Attack、AsyncThread、LifetimeThread、Signal。实际 DI 使用原生代理 **`NetRemote_3844293574`**。

独立库定向回归：

```text
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release
  --filter FullyQualifiedName~NetRpcWeaverTests --nologo
  --blame-hang-timeout 25s -m:1
Passed: 4; Failed: 0; Skipped: 0
```

这是本轮聚焦回归，不是新的全量 suite 计数。

### 真实 TCP+UDP smoke

通过 `BeginSmokeReadback()` 启动，Editor update 持续 Pump，再读 `LastSmoke`；不在主线程阻塞等待网络 Task。

```text
Status=RanToCompletion
PASS Unity 2022.3.62f3: proxy=NetRemote_3844293574; woven=True;
Plus=42; HP=80; X=128; List=1; Dictionary[1]=1; All=1/1;
async-main-thread=true; Changed-main-thread=true;
authority-denied=true; notifications=6
Active=False
```

覆盖接口 Task 返回、普通类 wrapper、UniTask 返回、异步业务 await 后主线程、UDP All 每端一次、ECS scalar 与接口 List/Dictionary、Client 写权限拒绝。

### Adapter / 生命周期

`AdapterLifecycleReadback()` 实际验证：

- worker 线程 ingress 先排队，源数组立即写坏后主线程仍收到 42；async outbound 同样持有独占副本。
- 257 包触发 item 上限，fault / Closed 各一次，失效数据不提交；超过 4 MiB 的单包触发 byte 上限，Closed 一次且队列字节归零。
- 32 个同时断线的 control 全部投递；32 个 late Closed 全部投递；第 33 个 transport 被拒绝。
- Dispose 取消正在等待的 async send、归还排队包；wire/dispatcher 退出后不调用业务，`pending=0; bytes=0`。单独先 Dispose dispatcher 也不会执行已排队业务。

`BeginLifecycleReadback()` / `LastLifecycle` 实际结果：

```text
PASS lifecycle: window-close cancels in-flight business;
pending request ended; TCP/UDP/proof workers completed;
queues=0; ticks/notifications frozen; fresh session smoke passed;
stop-during-connect completed.
```

此检查关闭真实 EditorWindow、await Session/connection Disposal，读回两端 `_receiveLoop`、`_udpLoop`、`_udpProofLoop` 都结束，随后重建连接再运行 smoke，也覆盖初始化期间立即 Stop。

### 真正域重载

在窗口 Ready 时调用 Unity `CompilationPipeline.RequestScriptCompilation()`，让 `beforeAssemblyReload` 运行退出。Ready 会话的退出只等待 socket/send worker；reload probe 用有界 2s 等待写入 SessionState。重载后 `ReloadReadback()`：

```text
ready=True; disposal-complete=True; pending=0; bytes=0; active=False
Active-after-reload=False
```

随后新会话 smoke 再次通过。MCP capture 的可见窗口显示两个实体及 HP、woven=True、queue=0；截图已在会话中返回。最新检查范围的 cached console error 为空。

## 验证边界与下一关

已完成当前宿主的 **编译/生成/ILPP、新 backend 同 Editor 双 Runtime 真实 sockets、主线程、队列/请求/连接退出、窗口重启及 Ready 状态域重载**。

仍未验收：两个独立 Unity 进程、Player/IL2CPP/裁剪、完整 Project B 游戏/场景生命周期、新 backend Unity Relay 或 LiteNetLib、真实公网/NAT、长时间高人数负载、Unity GC。初始化尚未 Ready 时的域重载也没有单独验收。旧 `GameRpcSession` 和整个业务网络没有在本轮迁移。

后续从 `UnityNetRpcSession` 的生命周期接入业务，按场景/世界租约增加身份与权限验证，再独立处理跨进程及 Player/AOT；不要借本页的 Edit Mode 证据勾选后续关卡。Runtime/Transport 性能优化继续由独立性能工作线负责。
