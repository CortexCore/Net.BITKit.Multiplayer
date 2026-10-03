# Unity Edit Mode Host/Client Probe

## 当前可用入口

Project B 已通过本地 UPM 导入 `net.bitkit.multiplayer`：

```json
"net.bitkit.multiplayer": "file:../../Net.BITKit.Multiplayer/Src"
```

在 **不进入 PlayMode** 的情况下打开：

- `Tools > BITKit > Multiplayer > Host`
- `Tools > BITKit > Multiplayer > Client`

操作：Host 点击 Start；Client 点击 **Copy open Host connection details**，然后 Start。复制的是端口/临时票据/scope，不共享网络状态或业务对象。每个窗口有独立 Runtime、wire 和 DI provider，transport factory 使用 native UDP。

Host 自动以约 20 Hz 发送二维轨迹，Client 绘制实际收到的位置与序号。Client 的 Add(1) 是可靠单向 Host RPC，Read() 是 Task<int> RPC。UDP enabled 只切换 gameplay UDP；暂停时可靠查询应继续工作，恢复后位置继续。Stop/关闭窗口会停止节点；脚本重载、Editor 退出和进入 Play 前也会清理。

## 这次实际做了什么

- `Src/Editor/CodeGen/Weaver.cs` 是 CLI 与 Unity 共享的 C#9 变换器，新增 `WeaveModule(ModuleDefinition)`。
- `TypedRpcILPostProcessor` 是实际 Editor ILPP，程序集为 `Unity.BITKit.Multiplayer.CodeGen`。
- Core 桥接类型/方法通过**目标程序集 Cecil 元数据**解析，不在 ILPP host 中反射加载含 ReadOnlySpan 的目标类型。
- Unity Bee 要求 PE/PDB 成对输出；adapter 写新的有效 Portable PDB，而非返回旧 PDB 或 null。当前没有恢复源 sequence points，完整源码级调试映射仍待做。
- 新增 native Transport asmdef，补齐 Core/TouchSocket/Editor 的显式引用。
- 普通 `[Rpc]` probe 服务在独立 `Net.BITKit.Multiplayer.Probes` 程序集中，接收体只更新带锁/Interlocked 的纯数据。Editor update 轮询并绘制，不从网络线程调用 Unity GUI，也没有修改场景 Transform。

## 复用的依赖

本次 MCP 读回的已加载版本：MemoryPack.Core **1.21.4**、DI/DI.Abstractions **9.0.0**、TouchSocket/Core/Dmtp **4.3.9**、Mono.Cecil **0.11.4**、Unity.CompilationPipeline.Common **2022.2.0.1**。

MemoryPack/DI/Unsafe 等使用项目已有 NuGetForUnity 安装；TouchSocket 家族使用现有 `Assets/TouchSocketLab/Plugins`。其插件设置要求显式引用，因此 package adapter asmdef 使用 precompiledReferences。没有为了本次 probe 降级项目 DI、安装重复 TouchSocket DLL 或改账号后台。

**这是在当前 Project B 依赖环境下接通，不是一个完全自带第三方依赖的独立安装包。** 新项目仍需安装依赖及 Editor 编译 API。Runtime Core 不依赖 Newtonsoft。

## 最小验收记录：2026-09-28

实际 Editor：**Unity 2022.3.41f1**，项目 `Com.Project.B.Unity`，始终 `IsPlaying=false`。

修复导入兼容问题并完成 domain reload 后，MCP 读回：

```text
Result       = Passed (windows remain running)
RpcWoven     = true
HostReady    = true
ClientReady  = true
HostCount    = 1
ClientCounter= 0
RemoteValue  = 1
HostUdpSent  = 298
ClientUdpReceived = 298
LastSequence = 303
Freeze       = true
Resume       = true
IsPlaying    = false
```

随后两个 EditorWindow 截图均显示发送/接收 761、位置序号 766，点的位置一致。计数会随继续运行增长，不把这几个数字当成固定断言。接收服务记录的 Add sender 与 Client Peer 一致。

本轮只做了包导入/Unity 编译、一次真实双端 Add/Read 与 UDP 暂停/恢复，以及窗口读回。没有追加完整回归、Relay、PlayMode、Player、IL2CPP 或性能基准。既有项目警告不等于本包编译失败。

## 代码入口与下次工作

Project B 后续增加了 `Tools > BITKit > Multiplayer > Component Probe`，将真实 NetworkTransform / NetworkRigidbody 显式绑定到这对测试 Runtime，在 PreviewScene 验证组件快照、UDP 暂停/恢复和物理角色恢复。宿主说明为 `Com.Project.B.Unity/Docs/network-component-il-rpc.md`。包提供 `TryGetRuntimes` / `SetHostUdpEnabled` 测试接线 API；组件实现仍属于宿主，包不反向依赖 Project B。

- `Src/Unity/Probes/RpcProbeService.cs`：Add、Read、Pose 的普通业务方法。
- `Src/Editor/Probe/ProbeNode.cs`：独立会话、开发用 loopback admission、ready 与关闭。
- `Src/Editor/Probe/ProbeWindows.cs`：界面、Editor update、短 smoke 状态机。
- `EditorProbeDiagnostics.StartSmoke()` / `GetStatus()` / `Stop()`：异步 Editor 自动化入口；不要同步阻塞 Editor 等待 Task。
- `Src/Editor/CodeGen/TypedRpcILPostProcessor.cs`：Unity 编译入口。

下一步按实际需要接入可见 Transform/主线程 scheduler 或增加 SyncVar probe。本轮没有验证 SyncVar、接口 DI alias、场景生命周期或 AOT；[分阶段手册](unity-integration-plan.md) 的这些关卡仍保留。
