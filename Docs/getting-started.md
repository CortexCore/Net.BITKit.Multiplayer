# 快速开始：当前 .NET 用法

这是独立库用法。Unity 当前宿主的接入边界见 [当前状态](current-status.md)、[新 NetRpc 验收](unity-netrpc-validation.md) 和 [接入手册](unity-integration-plan.md)。

## 构建与测试

使用 .NET 10 SDK 构建工具、样例和测试。共享 Core 为 `Projects/BITKit.Multiplayer.csproj`，程序集名 `Net.BITKit.Multiplayer`；原生网络实现为 `Projects/BITKit.Multiplayer.Transport.csproj`。

```powershell
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo
dotnet test Net.BITKit.Multiplayer.slnx -c Release --no-build --nologo
```

## 运行 NetRpc Sample

```powershell
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release -- --relay
```

两条命令分别验证原生 Direct 和 Relay。构建会生成 Contracts 的远程接口代理并编织普通类 RPC；成功时输出 `PASS Direct` 或 `PASS Relay`。业务层不直接选择 socket 或编写 packet，连接和 Relay sidecar 只存在于 composition root。

可视化跨进程验证使用 `Start-Godot-Sync-Lab.cmd`；具体入口见 [Godot Sample](../Samples/NetRpcGodot/README.md)。

## 最小接线

Host 创建 listener、接受一个已通过应用层准入的连接，并把 Transport 交给房间作用域：

```csharp
using System.Net;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;

using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
var accepting = listener.AcceptAsync();
await using var clientTransport = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port);
await using var hostTransport = await accepting;

using var host = new ServiceCollection()
    .AddNetRpcService<IGameState, GameState>()
    .AddNetRpc(true, _ => hostTransport, scope: 42)
    .BuildServiceProvider();

using var client = new ServiceCollection()
    .AddRemoteInterface<IGameState>()
    .AddNetRpc(false, _ => clientTransport, scope: 42)
    .BuildServiceProvider();
```

应用仍须在 `AddNetRpc`/`AttachPeer` 前验证账号、房间票据和权限。TCP+UDP proof 只证明本次 Transport 的端点归属，不替代用户认证。

## 业务规则

- Remote interface 使用 `AddRemoteInterface<T>` 和 `AddNetRpcService<TContract,TImplementation>`，调用端不需要实现类型。
- 普通类 RPC 使用 `[Rpc]` 并在构建期通过 `CodeGen --netrpc` 编织；运行时不执行 Weaver。
- Host 与 Client 角色互斥。Host 是权威端，不创建隐含本地 Client。
- `void` 是单向通知；Task、ValueTask 和 UniTask 返回值保留完成、错误和取消语义。
- 状态、ECS 和网络集合由 Host 发布；Client 本地写入明确失败。
- Direct 和 Relay 对 Client 使用相同 `TcpTransport.ConnectAsync` API；Relay 不改变业务权限或目标语义。

完整 DI、生成、状态和生命周期说明见 [实现与使用](design-implementation.md)；传输边界见 [Transport](transport-guide.md) 和 [Relay](relay-guide.md)。
