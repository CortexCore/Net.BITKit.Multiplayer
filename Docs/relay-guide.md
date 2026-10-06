# NetRpc Relay

当前 Relay 位于 `Src/Transport/NetRpcRelay.cs`，由三个部分组成：

- `RelayEndpoint`：接受一个 Host sidecar 和普通 Client 连接，按分配的 peer ID 转发可靠/不可靠帧。
- `RelayHostConnection`：Host 主动连接 Relay，并把 Relay 加入/离开的 Client 附着到 `RpcContextService`。
- `TcpTransport`：Client 使用与 Direct 相同的连接 API，不需要 Relay 专用业务接口。

Relay 只转发 NetRpc 帧，不执行业务方法，也不改变 scope、目标、权限或 Host 权威语义。

## 运行样例

```powershell
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release -- --relay
```

核心接线与 Sample 一致：

```csharp
await using var relay = new RelayEndpoint(new IPEndPoint(IPAddress.Loopback, 0), "host-key");
await using var sidecar = new RelayHostConnection(hostRuntime, "127.0.0.1", relay.EndPoint.Port, "host-key");
await using var clientTransport = await TcpTransport.ConnectAsync("127.0.0.1", relay.EndPoint.Port);
```

Host key 只用于让当前 Relay 实例识别 authority sidecar，不是玩家账号系统。生产接入必须在 Relay 外层提供真正的房间预留、短期票据、TLS/受保护网络和滥用限制，不能把样例字符串当作公网凭据。

## 生命周期

- Relay Host 断开时，已路由 Client 从 Host Runtime 分离。
- Client 断开时，Relay 通知 Host sidecar，Runtime 释放对应 peer。
- Host 和 Client 都从本机主动连接 Relay；当前实现没有打洞、Host migration 或 Direct 自动回退。
- `RelayEndpoint` 和 `RelayHostConnection` 都是 `IAsyncDisposable`，关闭时应等待 worker 退出。
- 不可靠数据继续走 `SendFast`，不会因经过 Relay 自动变成可靠调用。

## 当前边界

当前实现和样例主要用于 loopback/开发验证。公网部署前仍需补齐并实测：

- TLS 或等价的受保护传输。
- 房间目录、短期 Client 票据和 Host 凭据轮换。
- 明确的连接数、速率、队列和 payload 限制。
- Linux 服务生命周期、跨 ISP/NAT、断线恢复和长时间负载。

回归入口为 `Tests/NetRpcRelayTests.cs`、`Tests/NetRpcDesignTests.cs` 和 `Samples/NetRpc --relay`。
