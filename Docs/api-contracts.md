# API 与内存契约（当前）

## 新 NetRpc 入口（2026-10-03）

BITKit.Multiplayer.NetRpc 已实现手写 design-v1 v1～v5 与 design-v2，使用 MessagePack、生成接口、CodeGen --netrpc 编织、原生 TCP+UDP/Relay。通过 AddRemoteInterface<T> / AddNetRpcService / AddNetRpcObject 的 DI 和 IEntitiesService 接线，业务不 Bind*。[新链路指南](design-implementation.md) 包含准确的借用内存、同步容器、权限和帧边界。

当前 worktree 默认异步栈为 **UniTask 2.5.10**：`RpcContext.Request/Request<T>`、receiver、发布与传输均返回 UniTask，必须消费一次；显式 `RequestTask/RequestTask<T>` 提供可重复 await 的 Task 兼容表面，`RequestValue` 为 ValueTask 兼容表面。Task/ValueTask-authored RPC 仍支持，但默认 UniTask 链路无中途 AsTask。类型化回复在借用回调返回前解码；参数在发送入口冻结；超时/取消不提前释放仍在发送的 buffer；约 10ms 共用扫描超时。UniTask 不自动流动 ExecutionContext/SynchronizationContext，跨 await 的业务身份需在入口保存不可变 `NetRpcCallContext.Current`；Unity 对象需要显式主线程适配。[API 升级、线程规则与实测](netrpc-unitask-default.md)。

下面 RpcRuntime/B6/IRoomWire/TouchSocket 契约用于旧 backend，不能将其 Bind、编码、字节上限或平台证据套到新 backend。

此页概括使用约束，不是所有 public method 的自动生成 reference。准确签名以所列源码为准；实现细节看 [typed 指南](typed-rpc-guide.md) 与 [Transport 指南](transport-guide.md)。

## 作者表面

| API | 语义 |
| --- | --- |
| `[Rpc(SendTo.Host)]` | 发往权威 Host；已经在 Host 时本地执行 |
| `[Rpc(SendTo.All)]` | 只能 Host 发起，Host 和就绪 Clients 各执行一次；只允许 void |
| `[Rpc(SendTo.Target)]` + `RpcTarget` | 显式单目标；Client→Client 经 Host，保留真实来源 |
| `Delivery = RpcDelivery.Unreliable` | 只允许 void；无重传/结果等待/可靠回退 |
| `void` | 单向通知；本地验证失败仍抛出；远端 handler 异常由接收端诊断 |
| `Task` / `Task<T>` | 远端完成/结果与错误；取消等待不撤销远端业务；超时可能已执行 |
| `[SyncVar]` | Host 权威的标量自动属性、整体替换；getter-only SyncList/SyncDictionary/SyncHashSet 使用快照和有版本增量。普通/嵌套集合及深层变更观察不支持 |
| `[Hook(nameof(...))]` / `Changed` | 标量 old/new 与类型化集合变更；编织校验、锁外通知、异常隔离。首次集合快照 Reset，对象初始化使用 Runtime.Synchronized |
| Unity NetworkBehaviour | Unity-only 生命周期适配，复用现有 RPC/SyncVar；Client 等待初值后 OnStartClient，disabled 不解绑；见 [说明](unity-networkbehaviour.md) |
| `[HostOnly]` / `[ClientOnly]` | 仅本地角色检查，不产生网络发送 |

普通实现对象须经过编织并 Bind 后再调用 RPC，具体实例和接口调用一致。Unsupported signature 在构建期诊断：例如 open generic 实现/方法、ref/out/in 参数、async void、Task All、woven CancellationToken、尚未支持的 DTO/状态形态。声明一个接口 proxy 不等于普通生成调用；`CreateProxy` / `CallAsync` 是有反射/分配成本的兼容表面，AOT 不能默认可用。

定义：`Src/Runtime/Contracts.cs`；生成验证：`CodeGen/Program.cs`。

## 角色、身份和 DI

- `RpcRuntime` 对应一个房间 scope/epoch，拥有成员、绑定、pending 和状态；不能注册成跨房间的 current-runtime singleton。
- `INetworkContext` 是该实例的只读角色/目录视图。业务 PlayerId/SteamId 与连接 PeerId、实体 TargetKey 是不同概念。
- `INetworkOwnership` 是由实体 adapter 提供的只读 OwnerPeerId/OwnershipChanged。Unity NetworkBehaviour 据此计算 IsOwner；Project B 经 NGO3 roster 复制。不要与 Bind 的 owner 参数混用：后者同时限制 RPC 和状态接收，仍保留私有数据语义。
- `AddScopedRpcService<TContract,TImplementation>` 将接口和实现绑定为同一 scoped 对象。服务解析前需要该 scope 的 Runtime 已创建。
- `ITransportFactory` 可以是无状态 singleton；`Create()` 必须产生独立、由 lane 拥有的 endpoint。不要由 root provider 长期持有每次重绑的 transient socket。
- 接收身份来自已认证 wire。`RpcTarget` 只负责路由，不授予权限。Host 的 `authorize` 或 owner 策略必须明确；`$sync.state` 单独决定状态可见性。
- 不需要网络环境的普通本地业务方法不加 Rpc；未绑定或 Offline 下的 RPC 不悄悄退化为本地业务调用。

入口：`DependencyInjection.cs`、`RpcRuntime.Bind/RegisterMember/CreateProxy`、`TransportContracts.cs`。

## 准备与退出

```text
可靠连接 + 应用认证
    → Host 认可 Peer / 成员目录
    → UDP 会话凭据与端点 proof
    → Client 安装服务/目录并确认 ready
    → Host 放行广播
```

`ConfirmReady()` 只记录客户端确认，不允许 Client 自行授予 Host 成员资格；要等权威目录和实际 `IsReady`。TCP/DMTP verify token 不是用户认证。Arena 的完整同连接 ready/leave 流程在 `Samples/Arena/Game/ArenaSession.cs`，不能把示例登录服务当成生产账号系统。

| 操作 | 结果 |
| --- | --- |
| `Unbind(key)` | 移除实例绑定，保留同一逻辑身份的状态供重绑定 |
| `RemoveTarget(key)` | 当前 scope 中永久墓碑；迟加入也获知删除，不能同 scope 重用 key |
| 成员 demote | 连接可保留，但不 ready；需要重新授权就绪 |
| 成员 remove | 当前连接身份终止；重新认证使用新 PeerId |
| `RpcRuntime.Dispose()` | 解绑、结束本地等待、取消订阅并调用所持 wire 的 Dispose；不是强制归还仍在 I/O 中的借用内存 |

Unity 主线程队列还必须携带 scope/target 身份并在执行前复核，防止新地图收到上一轮排队调用。Project B 宿主现提供 `IGameRpcSession` / `GameRpcSession` 的有界 main-thread wire、绑定租约和 world cancellation；这是宿主适配器，不是 Core 自动为任意调用者切线程。本地 Host 调用仍应从主线程发起，后台工作使用宿主 TryPost。完整 Player/场景验收仍见 [Unity 手册](unity-integration-plan.md)。

### Project B 领域 Agent 的入口

在宿主注入 `Project.B.Multiplayer.IGameRpcSession`，等待 IsReady/Changed，再调用 BindService、BindEntity 或 BindComponent，保存并释放返回的 IDisposable 租约。Host owner/authorize 必须明确；对象身份来自既有 Host roster。地图结束统一撤销绑定；新 world 使用新的 Host generation。不要从 EditorWindow 获取游戏 Runtime。

实现位于宿主 `Assets/ProjectBObservation/Game/GameRpcSession.cs`；接口位于 `Assets/Artists/Scripts/Multiplayer/IGameRpcSession.cs`。HostObservationService、ClientMode/ClientProgram 已接入生命周期，NetworkGameObjects 对现有同步组件接入绑定。门/库存/战斗等具体业务由领域实现决定。

当前通过已有 Observation Join 获取一次性票据，连接 base port + 2 的 BITKit sidecar；并非已把新协议复用到旧物理 Socket。启动/授权细节留给统一接线层，领域代码只依赖上述接口。现有 Edit Mode fixture 已验证真实 admission、主线程 RPC 和 world 清理，未验证实际整场 Play。

## 三层传输接口

| 接口 | 边界/内存 |
| --- | --- |
| `ITransport` | 一个 packet endpoint；Received 的内存仅在同步 callback 内借用；SendAsync 到真实完成才结束借用 |
| `IRoomDatagrams` | 已认证 Peer 的 UDP lane；保留绑定/HMAC/replay/expiry，不可绕过身份层直接按包头 PeerId 路由 |
| `IRoomMemoryWire` | 可靠 room memory lane；`MemoryReceived` 借用到 callback 返回；`SendMemoryAsync` 的 ValueTask 必须消费一次 |
| `IRoomWire` | byte[] 兼容 wire；纯旧接口会触发明确复制，不是 typed 零分配路径 |

`ITransport.Dispose()` 发出停止信号，`StopAsync` / `Completion` 等待实际操作释放；不能在当前 receive callback 内同步阻塞等它自己结束。Relay 队列有上限，active frame 在底层 DMTP Task 结束前仍需保留；通知调用方断开不授权提前归还 OS 正在引用的缓冲。

接口定义：`TransportContracts.cs`、`DatagramContracts.cs`、`RoomMemoryContracts.cs`。不要把这些接口分别当成三套可任选的认证协议。

## 参数、缓冲与上下文

- generated wrapper 在返回前同步编码源参数，调用者之后可复用原数组有效区间。一次扇出可共享不可变编码 frame，所有发送分支终止后才归还。
- 接收的 `TypedRpcReader` 是 ref struct，不能捕获到异步 lambda 或带过 await。生成器在调用业务体前完成参数读取；变长业务值拥有自己的对象/数组。
- `RpcCallContext.TryGetValue(out RpcCallValueContext)` 适合同步热路径；旧 `Current` class 视图可以分配。跨 await 使用正确的异步调用上下文，不保存可被复用的 mutable singleton。
- MemoryPack 的类型格式和 RPC 帧不同：B6 数值头负责路由，MemoryPack 只负责参数/值。可靠 reference DTO 使用默认 MemoryPackable + 连续 MemoryPackOrder；不等于允许任意多态/循环/自定义 callback。
- `TypedPacketBuffer`、`TypedRpcWriter`、`TypedRpcResults` 是生成器桥接设施；应用不要复制 owner writer、手动重复 Finish/Dispose 或自行操纵内部池。

## 固定边界与性能口径

- UDP：应用 payload 900 B、实际 wire 1200 B，无自动分片/可靠回退。
- 可靠 frame：1 MiB；SyncVar 编码值：16 KiB；数组/segment 每层 256 项、参数 32 个，另有深度/累计预算。
- 同步集合：每集合 256 项、每批 64 操作、Runtime 有界发送积压 128；指纹覆盖对象状态契约和成员 schema。完整用法、DTO 副本语义和升级要求见 [集合与 Hook](sync-collections-guide.md)。
- typed 在途 frame：每 Runtime 128；各底层 lane/Relay 队列有独立上限。
- 方法名字符串、状态/control model、反射注册仍可在非生成路径出现；0 B 只覆盖指定预热同步标量/固定 struct void 路径。

实际测量范围、残余开销与复现命令统一见 [typed-rpc-validation.md](typed-rpc-validation.md)，不要从本页推导未测量的 Unity GC 指标。
