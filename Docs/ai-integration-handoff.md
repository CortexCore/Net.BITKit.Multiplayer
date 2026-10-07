# AI 接入导航

先读 AGENTS.md、[当前状态](current-status.md)、[架构](architecture.md)，然后定点阅读。
唯一实现为 NetRpc；旧 RpcRuntime/B6、packet factory、room wires、旧同步集合与混合编织已删除，不能恢复。

| 任务 | 源码入口 | 验证 |
| --- | --- | --- |
| RPC／DI | Src/Runtime/NetRpcServices.cs、NetRpcV1.cs | NetRpcWeaverTests、NetRpcDesignTests |
| 生成／编织 | CodeGen/Program.cs、Src/Editor/CodeGen/NetRpcWeaver.cs、Weaver.cs | 默认 CLI、无后端标记 fixture、真实 woven 调用 |
| Unity ILPP | Src/Editor/CodeGen/NetRpcILPostProcessor.cs | 宿主 MCP 编译与包路径读回 |
| 状态／集合 | NetRpcState.cs、NetEntities.cs、NetworkCollections.cs | NetRpcDesignTests、NetworkCollectionHotTests |
| 请求／性能 | NetRpcRequests.cs、NetValues.cs | NetRpcHotspotTests、NetRpcUniTaskTests |
| Direct／Relay | Src/Transport/TcpTransport.cs、NetRpcRelay.cs | NetRpcDesignTests、NetRpcRelayTests、Samples/NetRpc |
| Unity 对象／线程 | 同级 Unity 扩展 Src/NetRpc/UnityNetRpcAdapter.cs、UnityNetRpcDispatcher.cs、UnityNetworkObjects.cs | MCP Edit Mode；不由 .NET 推断 Player/AOT |
| LiteNetLib | 同级扩展 AGENTS.md、README.md、Src/LiteNetLibDirect.cs | 独立测试和主库 wrapper 集成测试 |
| 业务案例 | Samples/NetRpcGodot/HUMAN-START-HERE.md、Session/HumanSetup.cs | Host／双 Godot 样例 |

普通 RPC 对象用 AddSingleton；IRpcContext<T>/IDisposable 作者约束仍在。接口服务 AddNetRpcService、
Client AddRemoteInterface 和 Task/ValueTask 显式互操作路径仍有效。

同级相对引用要求隔离 worktree 的三个仓库放在同一父目录、保留原仓库名。Unity 宿主不自动跟随 worktree；
验证时切换包引用并读回 resolvedPath，再恢复引用。

[删除与验证证据](legacy-backend-removal.md)。历史记录解释过去的实现，不是当前 API 指令。
