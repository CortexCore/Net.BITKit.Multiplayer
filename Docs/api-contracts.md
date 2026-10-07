# API 与内存契约（当前）

唯一后端为 `BITKit.Multiplayer.NetRpc`。普通 RPC 对象使用标准 `AddSingleton<T>()`，远程接口使用
`AddRemoteInterface<T>()`，Host 接口服务使用 `AddNetRpcService<TContract,TImplementation>()`。
旧 Runtime、对象注册快捷入口、room-wire/packet factory、B4/B5/B6 和后端选择标记已经删除。

| API | 语义 |
| --- | --- |
| `[Rpc(SendTo.Host)]` | Client 发往 Host；Host 本地调用直接执行业务体 |
| `[Rpc(SendTo.All)]` | 仅 Host 发起；Host 与当前 Clients 各执行一次；返回 void |
| `RpcDelivery.Unreliable` | 返回 void；不重传、不等待结果、不自动转可靠通道 |
| `void` RPC | 单向；没有业务成功 ACK |
| `UniTask` / `UniTask<T>` | 等待远端完成／结果；默认单次消费 |
| `Task` / `ValueTask` 返回值 | 当前互操作支持，仍走同一 NetRpc 后端 |
| `AddNetRpcRuntime` | 注册 Runtime、IRpcContext 与实体基础设施；不启动发布计时器 |
| `AddNetRpc` | 注册 Runtime、附着已连接 Transport；Host 自动启动状态发布 |
| `RegisterTarget` / `AttachPeer` | composition/adapter 的底层接线入口 |
| `IEntitiesService.Register` | 注册带身份和 INetComponent 的 NetEntity |

普通类编织仍需源码构造参数 `IRpcContext<T>` 和显式 `IDisposable.Dispose()`。接口代理由构建期生成，
或 .NET 可选 RemoteCompiler 生成。Unity 编织自动处理 Rpc，无需后端选择属性。

## 请求、身份与传输内存

Host 是权威 Peer 1，为各连接分配新的逻辑 Client ID；scope 必须匹配。调用者身份来自真实连接。
RpcContextService.Authorize 是应用权限策略，端点 proof 不是账号登录。

取消／超时不撤销已执行的业务，不提前归还仍在发送中的内存。Runtime Dispose 或连接断开结束 pending
请求并移除订阅。跨 await 的身份在入口保存 NetRpcCallContext.Current；Unity 对象访问通过主线程 Adapter。

ITransport.Send/SendFast 完成后才结束 ReadOnlyMemory 借用。OnReceived 每次交付完整帧，内存只借用到同步
回调返回；异步持有时复制。发送完成不代表远端业务完成。不可靠超限明确拒绝，没有自动可靠回退。

## 状态与 Unity 生命周期

Host 接口 getter 在 Client 读取本地缓存；初始快照前是默认值。IList/IDictionary 契约使用
NetworkList/NetworkDictionary。Client 修改失败，版本断档请求快照，非法更新不部分提交。

NetEntity 的 INetworkIdentity.EntityId 定位对象，各端注册相同 ComponentId/schema 的组件。Host 写
NetComponent<T>.Value，Client 消费 Changed，周期快照修复丢失更新。旧 SyncVar/Hook 和旧同步集合已退役。

UnityNetRpcAdapter 每帧主线程 Pump。UnityNetworkObjects 负责场景／Prefab 生成、Owner 和销毁，
业务组件从句柄接到 NetEntity。退出时注销实体、Dispose Adapter、等待 Disposal，再释放房间 DI 和对象资源。

[实际用法](../README.md) · [传输](transport-guide.md) · [Unity 接入](unity-integration-plan.md)
