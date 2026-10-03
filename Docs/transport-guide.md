# ITransport / DI 与原生 UDP

## 当前分层

```text
RPC / SyncVar / Peer / 房间权限
           │
    已认证的 room wire
      ┌────┴────────────────┐
      │                     │
可靠连接/控制面         UdpLane 会话数据面
TouchSocket DMTP        HMAC、端点绑定、重放窗口
      │                     │
      │             ITransport / ITransportFactory
      │                     │
 TCP（可配置 TLS）       UdpTransport（默认）
                        System.Net.Sockets
```

`Src/Runtime/TransportContracts.cs` 定义包 I/O 契约，不依赖 TouchSocket 或 Unity。`Src/Transport` 是原生实现，程序集 `Net.BITKit.Multiplayer.Transport`，C#9、netstandard2.1/net8.0。可靠会话仍由 TouchSocket 提供；本次抽象不是把原有登录、RPC 权限及房间身份移入 Socket。

`ITransport` 负责一个端点的启动、发包、收包、能力、统计和关闭；`ITransportFactory` 为每个房间通道及每次重绑创建独立端点。以后可实现其他包协议。能力必须明确声明，当前原生 UDP 只支持 `Unreliable`；要求它可靠发送会失败。仅提供可靠模式的 KCP 实现不能直接代替当前 Unreliable lane；KCP/RUDP 的可靠通道接入、调度和协议协商仍需单独实现，不能因为有接口就声称支持。

## 为什么仍保留 UDP 绑定

TouchSocket 可靠连接已经完成身份认证，可复用来下发 UDP 凭据与协调重绑，**无需再实现第二套账号登录**。但 TCP 连接不能证明 UDP 端点：TCP/UDP 的 NAT 映射可能不同，UDP 也可能被阻断。

因此仍保留：

1. 通过可靠通道下发当前会话的 UDP 凭据。
2. UDP challenge/proof，验证实际来源 IP/端口与回程可达性。
3. 成功后才将 UDP 通道标记 ready。
4. HMAC、重放窗口、心跳和过期；重绑经可靠通道换新凭据，旧端点不能重新夺回绑定。

原生 Socket 已绑定本地端口只意味着可以开始收发，**不等于某个远端 Peer 已认证或可达**。HMAC 提供认证，不是加密；公网控制面保护要求继续适用。

## 使用 DI

```csharp
using BITKit.Multiplayer;
using BITKit.Multiplayer.Transport;
using BITKit.Multiplayer.TouchSocket;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddUdpTransport(); // TryAddSingleton<ITransportFactory, UdpTransportFactory>
using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<ITransportFactory>();

using var hostWire = new TouchSocketHostWire(factory);
// await hostWire.StartAsync(...): reliable admission remains application-owned.
```

替换工厂，例如显式选用旧实现做性能对照：

```csharp
services.AddSingleton<ITransportFactory, TouchSocketUdpTransportFactory>();
services.AddUdpTransport(); // TryAdd 不覆盖前面的自定义注册
```

自定义实现同样注册 `ITransportFactory`，由 `Create()` 返回新的 `ITransport`。不要把一个有状态的已连接 endpoint 注册成全局 singleton，也不要让根容器跟踪每次重绑产生的 disposable transient。工厂可以是无状态 singleton，端点由 room lane 明确拥有、关闭和释放。

支持注入的入口：

- `TouchSocketHostWire(factory)`
- `TouchSocketClientWire(hostPeerId, factory)`
- `RelayHostWire(factory)`
- `RelayClientWire(hostPeerId, factory)`
- `TouchSocketRelayServer(authorizer, factory)`

这些参数可省略，默认原生 UDP。重绑继续调用同一个注入工厂创建新端点；没有绕过 DI 悄悄创建旧实现，也没有失败后自动切回 TouchSocket UDP。

## 内存、关闭与边界

- 一个原生端点租用一个 **65,536 B 接收缓冲**，接收回调返回后再复用，不每包新建。使用完整容量避免把超大报文截断成看似合法的 1200 B 包。
- `Received` 内存只在同步回调内有效；需要延后处理必须先复制/解码。
- `SendAsync` 在 OS 操作结束前持有调用方内存。数组-backed memory 直接借用；其他 memory 可临时租用并复制。取消不能提前归还 OS 仍引用的内存。
- `Dispose()` 只发出停止信号，可从接收回调调用；`StopAsync()` / `Completion` 用于等待接收与发送操作终止并释放资源。不要在当前接收回调内同步阻塞等待它自身退出。
- 重用 `SocketAsyncEventArgs` 及其发送 operation；同步接收完成采用有界批次后让出执行，不递归回调或每包创建 Task.Run。同步发送成功返回完成的 ValueTask，备用 TCS 只在真正 pending 时发布；已发布的 Task 不复用。异步发送与真实 socket 仍有分配，未承诺零 GC。
- UdpLane 按 grant 复用 HMAC，签名、验签、释放由同一 registry 锁保护；I/O 和用户回调仍在锁外。nonce 耗尽会撤销 grant，需可靠控制面换新凭据，不能回绕复用。
- 裸传输最大 IPv4 UDP payload 为 65,507 B；房间配置仍限制 **1200 B wire / 900 B RPC payload**。超限与不支持的 delivery 明确拒绝。
- 默认裸传输并发发送上限 128；当前 room lane 将它配置为 64。上层 runtime 另有 128 个调用上限、Relay 转发 64 个上限。不同层计数不能相加当作同一种请求数。
- Windows UDP 的 ICMP port-unreachable 不会因为一个已离开的 Peer 而关闭整个房间 UDP Socket；已加入针对性实测。其他致命接收错误仍终止端点，不无限重试。
- `TransportStatistics` 记录本实现的租还、在途和包统计，不覆盖业务 DTO、运行时全局 ArrayPool 缓存或所有操作系统缓冲。

## Sample 与对照

默认 `Start-Arena.cmd` 已使用原生 UDP；也可显式选择：

```powershell
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -Transport native
powershell -NoProfile -File Samples/Arena/Start-Arena.ps1 -Transport touchsocket

dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --transport native
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --transport touchsocket
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay --transport native
dotnet run --no-build -c Release --project Samples/Arena/E2E/Arena.E2E.csproj -- --profile --relay --transport touchsocket
```

App、Relay、E2E 均接受 `--transport native|touchsocket`。节点报告的 `TransportName` 来自实际注入工厂，分别为 `native-udp` / `touchsocket-udp`。旧适配器保留用于显式兼容/性能对照，默认路线不使用它。

本机验收与 A/B 数字见 [transport-validation.md](transport-validation.md)。Unity/IL2CPP、Linux socket 行为、公网/NAT 和长期饱和负载尚未验收。

2026-09-28 隔离 GC worktree 的优化、真实 socket 对照和健壮性回归见 [network-gc-isolated-validation.md](network-gc-isolated-validation.md)；代码已合入主库并完成 [Unity Edit Mode 会话验证](unity-sync-collections.md)。
