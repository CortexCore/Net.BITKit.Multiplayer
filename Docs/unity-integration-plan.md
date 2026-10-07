# Unity NetRpc 接入

Unity 适配为独立仓库 `Net.BITKit.Multiplayer.Unity`，主库不再内置旧 NetworkBehaviour/B6 接入。

## 包与编织

- 安装主库和 Unity 扩展的 Src 包；第三方依赖为 UniTask 2.5.10、MessagePack 3.1.8 及 DI。
- 主库不再依赖 MemoryPack；宿主其他功能的 MemoryPack 依赖不属于此次网络库清理。
- 消费方 asmdef 引用 Core/Contracts，业务访问 Adapter 时引用 Unity 扩展。
- NetRpcILPostProcessor 自动编织引用 Core 的程序集中的 Rpc，没有后端选择标记或旧分流。
- 普通类源码构造参数 IRpcContext<T> 和显式 IDisposable 约束仍保留。
- 接口用 NetRpcRemoteContractAttribute 指定可写输出路径，由 Editor 生成原生代理。

## 会话

1. 用普通 AddSingleton 注册业务 RPC 对象；接口 Host 注册 AddNetRpcService，Client 注册 AddRemoteInterface。
2. 调用 AddNetRpcRuntime，创建 DI 容器，解析 RpcContextService 和业务对象。
3. 在主线程创建 UnityNetRpcAdapter，通过 AttachPeer 包装已经建立的 Transport。
4. 在 Update 每帧调用 Adapter.Pump(Time.unscaledTimeAsDouble)，由 Adapter 主线程发布状态。
5. Prefab 的 NetworkIdentity 指定地址，实现 INetworkPrefabLoader；场景对象使用 SceneIdentity。
6. AttachNetworkObjects 后建立对象世界；Host Spawn/SetOwner/Despawn，Client Synchronize。
7. 将身份和 NetComponent 注册为 NetEntity；动态对象在 Initializing 接线，场景对象在 Spawned/已有 Objects 接线。
8. Despawning 时注销实体并释放容器；退出时 Dispose Adapter，等待 Disposal，再释放房间 DI。

对象 roster 同步初始状态、归属和生命周期；持续位置／生命值由组件样本同步，Identity 本身不持续同步 Transform。

Editor Lab：Tools/BITKit/NetRpc/Open Unity Sync Lab，源码在 Unity 扩展 Src/Editor/UnityNetRpcLab.cs。
本轮证据见 [旧后端删除](legacy-backend-removal.md)。旧 Unity 验收页中的 Session 名称和 Src/Unity 路径为历史布局。
