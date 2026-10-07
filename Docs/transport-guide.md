# NetRpc Transport

## 当前分层

`BITKit.Multiplayer.NetRpc.ITransport` 是新 backend 的字节传输边界：

```text
业务接口 / woven RPC / 状态 / ECS
                 |
        RpcContextService
                 |
             ITransport
          /       |       \
 TcpTransport  Relay   LiteNetLib Direct
   TCP+UDP     TCP+UDP       UDP
```

接口只有同步借用的接收事件、可靠 `Send` 和不可靠 `SendFast`。发送返回的 `UniTask` 完成后，上层才可释放 payload；`OnReceived` 的内存在回调返回后失效，需要异步保留时必须先复制或完成解码。

## 原生 Direct

`Src/Transport/TcpTransport.cs` 同时拥有：

- 长度前缀 TCP 可靠帧。
- 与该连接协商的一次性 UDP token 和端点 proof。
- `SendFast` 的不可靠 UDP 数据面。
- 有界未订阅接收 backlog、关闭事件和异步释放。

最小连接：

```csharp
using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
var accepting = listener.AcceptAsync();
await using var client = await TcpTransport.ConnectAsync("127.0.0.1", listener.EndPoint.Port);
await using var host = await accepting;
```

应用必须在把连接交给业务 Runtime 前验证账号、房间和票据。UDP proof 只证明 TCP 会话持有者可从当前 UDP 端点收发，不是玩家登录或业务授权。

`TcpTransportListener.AcceptAsync(TimeSpan handshakeTimeout, CancellationToken)` 为每个已接受连接单独限制 proof 时间；失败只关闭该连接，不停止 listener。

## 生命周期和内存

- `Send`/`SendFast` 的 payload 借用持续到返回的 `UniTask` 完成，包括失败和取消路径。
- 接收 callback 必须同步消费借用内存；Runtime 在 callback 返回前完成头和参数解码。
- `DisposeAsync` 等待 TCP、UDP 和 proof worker 退出。不要从当前接收 callback 同步阻塞等待自身退出。
- 可靠帧和 NetRpc payload 都有大小上限；超限、错误 scope、错误 schema 和不支持的不可靠返回值应明确失败。
- `SendFast` 只有在 `IsUnreliableReady` 后可用；上层不能自动把失败的不可靠调用降级成可靠发送。

## 其他实现

同级独立仓库的 `Net.BITKit.Multiplayer.LiteNetLib/Net.BITKit.Multiplayer.LiteNetLib.csproj` 提供 LiteNetLib Direct，Unity UPM 包根为该仓库 `Src/`。它保持相同 NetRpc `ITransport` 语义，但不表示已经实现 LiteNetLib Relay。安装与构建见 [扩展指南](litenetlib-guide.md)。

旧 packet endpoint、UDP factory 和 room-wire 接口已删除。当前唯一扩展边界是 `BITKit.Multiplayer.NetRpc.ITransport`，原生不可靠数据由复合连接 `TcpTransport` 的 UDP 通道承载。

## 验证

```powershell
dotnet test Tests/TransportTests/BITKit.Multiplayer.TransportTests.csproj -c Release
dotnet test Tests/BITKit.Multiplayer.Tests.csproj -c Release --filter FullyQualifiedName~NetRpc
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release
```

公网/NAT、TLS、长时间饱和负载、Unity Player 和 IL2CPP 需要分别验证；loopback 测试不能外推这些能力。
