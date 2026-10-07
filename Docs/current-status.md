# 当前状态与边界

## GitHub 最新基线整合（2026-10-07）

已将下列 GC/生命周期和对象协议改动整合到 GitHub `main` 的 `d57974b` 基线上，保留最新 LiteNetLib 独立仓库拆分。干净 checkout 验证补齐 solution 缺失的既有 MixedBackendFixtures 项目；完整 Release 构建通过，完整测试原样重试 **236 通过 / 0 失败 / 1 个既有跳过**，网络三进程 smoke **11/11**、独立 Transport guards **8/8**。首次 fixture 缺失和随后已有 rental 计数时序断言失败均在[整合验证记录](upstream-integration-20261007.md)说明。Unity 本轮仅静态检查，Editor/Player/IL2CPP 仍未验证；提交/PR 状态以 GitHub 为准。

## 网络对象协议抽取到源 Package（2026-10-06）

从已有 Unity `UnityNetworkObjects` 抽出 Core `NetworkObjectService`：Host 权威的对象 ID/代次/版本、生成/销毁、Owner、名册与迟加入、加载取消和过期回调都由源 Package 实现。Unity 保留 GameObject 入口薄包装；Godot 提供真实 PackedScene/Node3D、主线程和身份适配。复用既有 `IEntitiesService`，Client 注册后通过可靠完整组件状态建立 Ready 屏障，避免加载期间先到的 UDP 状态丢失；遗漏组件接线会显式失败。

完整 Release 构建通过（1 个既有 warning）；solution **236 通过 / 0 失败 / 1 个既有跳过**，其中新增 19 个对象生命周期和 10 个初始状态测试。实际独立 Host + 两 Client 的网络基准 smoke **11/11** 通过，属于回归正确性检查，不是新的性能提升测量。新的 Godot 引擎验收及复现命令见[聚焦记录](network-objects-validation.md)。本轮没有 Unity Editor，因此 Unity 仅静态源代码/接口审查，未声称 Editor/Player/IL2CPP 通过。新 Core 对象 RPC 目标与旧 Unity-only 协议不兼容，所有 peer 需一起升级。旧 GC/生命周期/基准改动已保留，没有提交或推送。

## 网络模块独立 GC 基准（2026-10-06）

已用无引擎/UI的 `Tools/NetRpcPerformance` 复验实际生成/编织 NetRpc、原生 TCP/UDP、独立 Host + 两 Client：优化前/最终优化后各三轮 33/33 场景通过，固定闭环节拍、实际调用/帧/回调与全线程托管分配都有原始记录。Scalar-state 私有比较缓存复用使 Host 每次相同编码长度更新准确减少 32 B；三进程合计该场景中位数 103.01 → 70.59 B/Host 修改。RPC 仍约 100 B/次，不是整体零 GC；其他场景和 idle-sync 的短窗口波动原样记录。

独立 transport-only 同进程诊断 42/42 通过、56,160 次独立校验投递，与完整 Runtime 分层看待。完整 Release 构建通过（1 个既有 warning）；完整测试原样重试 **207 通过 / 0 失败 / 1 个既有跳过**，首次 5 ms 时序断言失败及所有重试日志保留。此前 3D demo 的 UI 分配下降不能用作网络模块指标。[基准、代码改动、复现及全部边界](network-module-baseline-20261006.md)。没有提交或推送。

## 3D Godot GC 验证中的启动/退出边界（2026-10-06）

独立 `MultiplayerCharacters3D` 案例的 GC 检查发现两项生命周期竞态：后台同步在 Dispose 后恢复扇出时会误报自身 runtime 的 ObjectDisposedException；首个已缓存请求可在 DI singleton 工厂完成前同步重入。前者已用最小同步 worker 退出处理修复，后者在案例采用 `AddNetRpcRuntime` → 解析 runtime/业务目标 → `AttachPeer` → `StartSynchronization` 顺序规避，未声称所有 `AddNetRpc` 即时重放场景已修复。定向 Core 回归 **36/36**，随后完整 solution 回归 **196 通过 / 0 失败 / 1 项既有可选 benchmark 跳过**；案例实际 socket 生命周期含立即端口复用通过，断线期间 Broken pipe 发送诊断仍保留。[变更、失败复现与边界](netrpc-demo-gc-lifecycle.md)。GC 数值与 Godot 可见窗口证据由独立案例保留，不据此宣称 Unity/AOT 已验收或整体零 GC。

## LiteNetLib 独立扩展仓库（2026-10-07）

LiteNetLib Direct 已从主库 `Src/LiteNetLib` 和 `Projects/BITKit.Multiplayer.LiteNetLib.csproj` 提取到同级独立仓库 `Net.BITKit.Multiplayer.LiteNetLib`。新扩展的 UPM 包根为 `Src/`，根目录 `.csproj` 支持 netstandard2.1/net8.0；最新 UniTask 源码和源码/asmdef GUID 保持不变。旧 worktree 完整保留在 `Net.BITKit.Multiplayer.LiteNetLib.LegacyWorktree`，不是新发布仓库。

Core/native Transport 不引用扩展；主库解决方案、Godot Session 和集成测试改为引用同级扩展。适配器独立测试移到扩展，Godot wrapper 测试留在主库。Unity MCP 刷新和域重载完成，当前 Project B 2022.3.62f3 无编译错误；新扩展未安装到该宿主，不外推 Unity LiteNetLib/Player/IL2CPP。结构调整后的 .NET 验证结果见 [提取记录](litenetlib-repository-extraction.md)。

## 旧第三方 Transport 适配已退役（2026-10-06）

TouchSocket adapter、对应项目、Arena/ConsoleRoom/SyncCollections 样例和专用 Datagram/Relay 测试已从解决方案移除。当前可运行网络链路统一为 `BITKit.Multiplayer.NetRpc` 的原生 TCP+UDP Direct、原生 Relay，以及 LiteNetLib Direct；旧 `RpcRuntime`/B6 类型仅保留协议和 Unity 兼容代码，不再提供该第三方 socket backend。历史设计和性能页保留为日期化证据，不是当前构建或运行入口。

## Direct listener 预准入超时（2026-10-04）

`TcpTransportListener.AcceptAsync(TimeSpan handshakeTimeout, CancellationToken)` 给**已接受的连接**单独设置 TCP/UDP 端点 proof 截止时间；握手超时只关闭该连接，不停止可继续接收的 listener。既有 `AcceptAsync(CancellationToken)` 保持原语义。Transport 的 netstandard2.1/net8 Release 构建成功（仅依赖 Core 原有 CS0067 警告）；`NetRpcDesignTests` 真实 socket 先让坏连接超时、再从同一 listener 接入正常 Client：定向 **20/20**；Project B Unity 2022.3.62f3 Edit Mode 重新编译无错误，账号票据准入、Direct Host/Client 附着、租约和 FullProfile 版本指纹测试 **9/9**。公网抗恶意并发、完整游戏及 Player/IL2CPP 仍未验收。

## 给人类看的 Godot 新 NetRpc 案例（2026-10-04）

新增独立阅读入口 [小商店＋靶子](../Samples/NetRpcGodot/HUMAN-START-HERE.md)：接口 → Host 实现 → Godot 消费者，另附普通类 Rpc/ECS。启动 `Start-Godot-Human-Lab.cmd`，中文六步按钮，公共金币/背包和一个预先存在的靶子；启动用现有 AddNetRpc 自动接线，不重复 AllowContract/StartSynchronization。真实独立 Host + 双 Godot Client 的 headless/可见验证均通过，生成代理、编织、标量/列表/字典/ECS、UDP All 和 Client 写拒绝均读回；两端金币 7、苹果 1、HP 80。证据与限制在案例页，不把它当玩家权限/完整游戏迁移验收。

## 性能 / 默认 UniTask 已整合主工作树

`perf/netrpc-hotspots` 的优化已逐文件整合到主目录工作树，并保留并行 Unity Session/Dispatcher/ILPP/生成器改动。新 NetRpc 默认 UniTask 2.5.10；主目录实际 Direct/Relay scalar **96.098 / 96.064 B/op**（Host+Client/Relay 计量），组件/字典各 20000 回调正确。旧 adapter 移除前的回归为 295/2；当前精简后的完整 .NET 回归为 **193 通过 / 0 失败 / 1 个可选 benchmark 跳过**。合入后的 Unity **2022.3.62f3 Edit Mode** 实际编译、UniTask receiver/transport、原生代理/ILPP、TCP+UDP smoke、主线程、adapter 与窗口/请求/worker 退出生命周期均再次通过。[本次整合与证据](netrpc-main-integration.md) · [默认 API 与性能](netrpc-unitask-default.md)。

Git 默认主分支统一为 `main`，自建服务 origin 配置为 `http://home.atlasworks.cn:3000/root/Net.BITKit.Multiplayer`。下方及验收文档的 `master` / 无有效 HEAD 是整合当时的历史状态；当前版本以 `git log` / `git status` 为准。首次版本快照仅包含源码、配置、样例和文档，不纳入本机 Artifacts、编辑器/构建缓存或旧的 .NET 样例生成残留。

## Unity 新 NetRpc 已通过 Edit Mode 接入验收（2026-10-03）

Project B **Unity 2022.3.62f3** 实际导入/编译、assembly opt-in ILPP、原生 DI 代理 `NetRemote_3844293574`、公开 `UnityNetRpcSession` 已接通。真实 loopback TCP+UDP smoke 通过：Task/UniTask、ECS/标量/接口集合、All 每端一次、权限拒绝、业务 await 后与 Changed 主线程。窗口关闭、pending request/业务 lifetime 取消、TCP/UDP worker 退出、空队列、初始化期间 Stop、重建及 Ready 状态实际域重载后的新会话均读回通过；聚焦 .NET weaver 回归 **4/4**。完整接线契约、队列限制和证据见 [Unity 新链路验收](unity-netrpc-validation.md)，续接见 [进度交接](unity-netrpc-progress-handoff.md)。

同 Editor 双 Runtime 不是独立 Unity 进程或 Player。完整游戏/场景迁移、Player/IL2CPP/裁剪、新 backend Unity Relay/LiteNetLib、公网及 Unity GC 仍待验证。

## 已采集新链路的真实热点调用栈

实际 Host/Client EventPipe 捕获 scalar RPC 30000次、Component/Dictionary各20000次，窗口排除启动/预热/报告。已定位 RPC async状态机与取消等待器、TCP重复ReadExactly状态机、UDP接收数组/端点、ConcurrentDictionary.Keys/Values复制视图、ReceiveState闭包和reader。CPU采样多为IO/等待，未把等待样本误报为CPU占用。[调用栈证据和优先级](netrpc-hotspots.md)。

## 已整合 LiteNetLib / GC 基线 / 集合热路径收敛

LiteNetLib Direct（15 项适配器回归）和 `Tools/NetRpcPerformance` 真实进程基线已整合主目录。完成 26 个 native Direct/Relay 基线，再优化集合单增量的整容器复制：256 项字典分配约下降 94%，列表约下降 77%，保持非法数据不提交/版本/快照恢复。分项目限时回归合计 **282 通过 / 0 失败 / 2 项原有性能测量跳过**；主目录双 Godot 的 LL Direct 和 native Relay 均通过。[本轮证据与剩余项](netrpc-collection-gc-improvement.md)。

此 GC/LiteNetLib 检查点未覆盖 LiteNetLib Relay、LL 同负载 GC 对照、整体零 GC；后续 Unity 新 NetRpc 编译及 Edit Mode 证据见本页顶部。

## Godot C# 2D 同步测试场（2026-10-03）

`Samples/NetRpcGodot` 已通过独立 Host + 两个实际 Godot 4.7.2 .NET Client 的 Direct/Relay headless 跨进程复验；4.6.1 的可见窗口证据仍保留。覆盖移动、woven 攻击、拾取、标量/集合/组件、25% 丢包/延迟/乱序、权限拒绝、断线重连和槽位释放。[一键使用](../Samples/NetRpcGodot/README.md) · [实际证据](godot-sync-lab-validation.md)。默认 `Start-Godot-Sync-Lab.cmd` 启动可玩的双窗口。

最新完整 suite 251 通过、0 失败、2 项既有性能測量跳过。Godot 适配验证了 bytes-loaded 程序集中的原生代理查找；PeerDisconnected 支持 Direct/Relay 统一的业务租约清理。新 Game 保持纯 C#，Godot 只负责主线程显示和输入。

## 2026-10-03：手写 design-v1 / design-v2 新链路

两份手写设计的 .NET 功能已落地到 BITKit.Multiplayer.NetRpc，包括 design-v1 v1～v5、ECS、接口标量与 IList/IDictionary 同步。[使用与对照](design-implementation.md) · [验收](design-implementation-validation.md)。

入口：AddNetRpc / AddRemoteInterface<T> / AddNetRpcService<TContract,TImplementation> / AddNetRpcObject<T>，构建期 CodeGen --remote + --netrpc。默认自研 TCP+UDP 复合 Transport 和原生 Relay；代理真正编译、接入 DI，使用强类型接收器。Core netstandard2.1，Roslyn JIT 适配器在 UPM Src 之外。

定向 28/28、Direct/Relay 样例通过；当时完整 solution 构建通过，suite 250 通过、0 失败、2 项既有可选 benchmark 跳过。Project B Unity 2022.3.41f1 曾验证脚本编译和 DLL 加载：补齐 Core asmdef 的 MessagePack/Annotations/StringTools 显式引用后退出 Safe Mode。后续 **2022.3.62f3 新 backend Edit Mode 网络/主线程/退出验收已通过**，见本页顶部；Player/IL2CPP、真实公网仍未验收。下方 B6、Unity 窗口与旧性能属于兼容 runtime 记录，不是新链路的接线/运行验收入口。

**源码核对：2026-09-28。阶段：Unity Package / ILPP / Edit Mode 双端通信已最小接通；纯 .NET NetRpc v1 原型已新增 TCP loopback 验收。** [Unity 读回记录](unity-editor-probe.md)。本页是状态地图；具体语义看 [API 契约](api-contracts.md)，.NET 证据看 [验收](typed-rpc-validation.md)。

**同步集合、Hook 与 GC 优化已合入 master 工作树，并在 Unity 2022.3.41f1 Edit Mode 的真实游戏会话验证通过。** 三种集合、标量 Hook、Host 权威、主线程、world 解绑均已读回；主库集合回归 22/22。见 [主库/Unity 接入记录](unity-sync-collections.md)。

随后完成的旧 runtime [集合 GC 优化](sync-collections-gc.md)记录了当时的真实网络对照；相关第三方网络样例现已退役，数字只作为历史基线。

## 一张表看清实现

| 区域 | 当前状态 | 定点源码 |
| --- | --- | --- |
| 角色/DI/生命周期 | Runtime 按房间作用域拥有绑定；Host/Client 互斥 | `Src/Runtime/Contracts.cs`、`DependencyInjection.cs`、`RpcRuntime.cs` |
| RPC 编织 | CLI/Unity 共享 C#9 Cecil 变换；typed wrapper/直接业务体 receiver | `Src/Editor/CodeGen/Weaver.cs` |
| 编织入口 | .NET CLI + Unity ILPostProcessor；Editor 已编织 probe | `CodeGen/Program.cs`、`Src/Editor/CodeGen/TypedRpcILPostProcessor.cs` |
| typed 运行路径 | 数值头、指纹、泛型 MemoryPack 读写、租用 frame | `Src/Runtime/TypedRpc.cs` |
| void / Task | void 无结果等待；Task 请求保留完成/错误/超时 | `RpcRuntime.SendTypedVoid`、`SendTypedTaskCore` |
| 接收上下文 | 同步值上下文 + 异步 AsyncLocal；嵌套恢复有测试 | `RpcCallContext.TryGetValue` / `Run` |
| SyncVar | 标量整体替换；Host 权威 SyncList/SyncDictionary/SyncHashSet、Hook/Changed、契约指纹、快照/增量/恢复与原子批次；不追踪普通/嵌套集合 | `RpcRuntime.Sync.cs`、`SyncCollection.cs`、各容器、`SyncWire.cs`、Weaver |
| 网络时钟 | 会话级 NetworkTime.time、RTT估算校时、非缩放单调读数；Editor 双窗有每秒 SyncVar 时间 Label | [NetworkTime](network-time.md)、`RpcRuntime.Time.cs` |
| Unity 网络行为 | 薄 NetworkBehaviour、初始状态屏障、角色/解绑回调、OwnerPeerId/IsOwner/OwnershipChanged、Host Only 与 UnityEvent；真实 Edit Mode 三端会话通过 | [使用说明](unity-networkbehaviour.md)、`Src/Unity/Runtime` |
| 传输 | 新 NetRpc 使用自研 TCP+UDP Direct/Relay；独立 LiteNetLib 扩展提供 Direct 适配 | `Src/Transport`；同级 `Net.BITKit.Multiplayer.LiteNetLib/Src` |
| NetRpc 手写设计链路 | 动态补表、生成接口/DI/强类型接收器、普通类编织、TCP+UDP/Relay、ECS/标量/集合同步；28 个定向测试及 Direct/Relay 样例通过 | [实现](design-implementation.md)、[验收](design-implementation-validation.md) |
| 多传输房间 | `RoomTransportHub` 可同时挂 Direct/Relay/Replay/Bot；统一 Room Peer 路由，虚拟 Transport 与 Socket Transport 共用接口 | `Src/Runtime/RoomTransportHub.cs`, `RoomForwarding.cs`, `RoomPorts.cs` |
| 借用内存 | IRoomMemoryWire 与 NetRpc Transport 均要求底层真实写入结束后才能释放 | `RoomMemoryContracts.cs`、`TcpTransport.cs`、`NetRpcRelay.cs` |
| DTO | MemoryPack 1.21.4；可靠生成 DTO 需连续显式成员序号 | `ReliableValues.cs`、Arena Contracts |
| Unity / IL2CPP | 2022.3.41f1 Edit Mode RPC/UDP、三种集合/标量 Hook、主线程/world 生命周期通过；Player/AOT 未验证 | [窗口记录](unity-editor-probe.md)、[集合接入](unity-sync-collections.md) |

## 协议不是统一叫一个“V4”

| 帧 | 用途 |
| --- | --- |
| **B6 / v4** | 普通 woven Reliable 和 Unreliable 调用；88 B 数值头，kind 1 通知、2 Task 请求、3 UDP 通知 |
| **B5 / v3** | 成员/状态/移除、Task reply、显式反射兼容调用 |
| **B4 / v2** | 显式旧 Unreliable 兼容入口；普通 woven UDP 不走这里 |

当前校验的是**收到的调用与本地生成 descriptor 的 ID/指纹匹配**。没有 Host 下发全协议目录，也没有动态补表；不是连接时已完成全目录协商。scope/target/peer/method 是数值身份或冷注册计算的哈希，不是“所有 ID 都是 Host 连续分配的 int”。参与端需匹配构建。

本版 B5 状态新增 state-format-1 指纹头及 `stateDelta` kind 9。状态指纹还覆盖对象的完整已声明状态成员集合；旧 SyncVar payload 不互通，所有参与端需升级并重新编织。

## 工具和依赖

- Core：`Net.BITKit.Multiplayer`，C#9 / netstandard2.1；MemoryPack.Core **1.21.4**、DI.Abstractions **8.0.2**；**无 Newtonsoft Core 依赖**。
- Native Transport：netstandard2.1 / net8.0；LiteNetLib 适配使用其项目声明的 NuGet 版本。
- Weaver：CLI 使用 .NET 10 / Cecil **0.11.6**；共享 `WeaveModule` 供 Unity ILPP 使用。Editor 使用宿主 Cecil **0.11.4**，从目标元数据导入桥接类型，输出新 Portable PDB；源 sequence points 尚未恢复。
- Sample/测试：主要 net10，传输测试亦有 net8；Arena 使用 Raylib。它不是 Unity Runtime。
- MemoryPack DTO 使用其 Source Generator。**本库 RPC 生成器当前是 Cecil，不是 Roslyn RPC Source Generator。**

## Unity 接入状态

1. Project B manifest/lock 已注册本地 `net.bitkit.multiplayer` 包；Core/native/Editor/probe asmdefs 已补齐。
2. 复用现有 UniTask、MemoryPack、MessagePack 和 DI 依赖；没有重复安装或降级依赖。
3. Editor ILPP 不启动外部 dotnet。实际 probe 的 WovenTypedRpc marker 与远端行为已读回。
4. 已有真实 NetworkTransform/NetworkRigidbody 的 IL RPC preview，以及 Project B 的 `IGameRpcSession` 主线程 wire/绑定租约。HostObservationService/ClientMode 接入了房间与地图生命周期；真实 Observation Join→sidecar→主线程服务/组件的 Edit Mode fixture 已通过。完整游戏 Play 仍未验证，Core 对其他宿主也不会自动切线程。
5. 新 SyncVar/集合/Hook 已通过 Game Session Smoke 的实际 ILPP、主线程与 world 租约测试。PDB 源行映射、Player/IL2CPP、完整游戏生命周期与 UniTask 仍待推进。
6. 最小对象 Ownership 已接入：Core 只读 INetworkOwnership；Project B NGO3 roster 初始/增量归属、Host 分配/转移/离线释放，所有组件共享 source。对象所有权与原 Bind(owner) 的私有状态可见性分离。最新 Core 99 通过/1 benchmark 跳过，宿主新增 4/4 EditMode 回归，真实三端 smoke 全通过；完整玩家输入/动画仍未接线验收。

## 已有证据与不能外推的结论

- **GC 隔离阶段证据（代码现已合入主库）**：UDP 鉴权/发送热路径优化，定向回归 70/70；256 B 真实 socket 负载的三批中位数，native 240→72 B、鉴权 1648→72 B、Relay UDP 两跳 3400→176 B/投递。可靠 Relay 当时约 1.45 KB；离群批次与验证范围见 [隔离记录](network-gc-isolated-validation.md)。这些 .NET 分配数据不代表 Unity Mono 分配。

- 最后完整 .NET suite：**182/182 通过**；之后强化零分配断言的 TypedHotTests **12/12 通过**。
- 同步测试 wire、预热、固定容量、标量/固定 struct void 的单播/本地/广播：0 B 断言。
- 最新 Arena 全进程：Direct Host/Client **0.407/0.328 MB/s**；Relay **0.452/0.349 MB/s**。包括诊断和显示工作，不是纯网络预算。
- 内存并非全为零分配：变长 DTO、状态/请求回复、旧 proxy、真实 socket/Relay 操作和 Sample 报告仍有开销。
- Unity Edit Mode 新增一次 Add/Read/UDP 暂停恢复成功记录；本轮未重跑整个 .NET suite。Unity Player、IL2CPP、Linux、公网/NAT及长时间高人数负载尚未覆盖。

## 下一步已确定

主工作树合入与 Unity Edit Mode 集合接入已完成；业务可按需迁移低频状态，完整游戏/Player/IL2CPP 仍是后续关卡。纯 .NET 可运行 `Samples/NetRpc`，Unity 可运行现有 NetRpc smoke。见 [接入记录](unity-sync-collections.md)。
