# 手写设计的可运行 Sample

从仓库根目录运行：

```powershell
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release
dotnet run --project Samples/NetRpc/NetRpc.Sample.csproj -c Release -- --relay
```

构建自动生成 Contracts 的原生接口代理，并真正编织 Actor 的普通类 RPC。默认 Factory 使用编译后的代理，不需要 Roslyn JIT 适配器。

两模式都执行：接口 Plus=42；RPC 修改 Host Health/Items/Counts；具体 Actor 的 Fire/Read；自动 ECS 和接口状态同步；不可靠 All 广播；拒绝 Client 集合写入。最后输出 PASS，任一行为不符则失败。

`Game.cs` 是游戏业务，没有 Bind*、Transport、socket 或手写 packet。`Program.cs` 是 composition root，负责 DI、既有 Entity 注册、连接和可选 Relay sidecar。Client 对 Direct/Relay 使用相同 TcpTransport.ConnectAsync。

接口只读状态初始是默认值；Sample 等待同步条件，不用 RPC 完成冒充状态到达。新 backend 的完整使用、边界和平台证据见 [指南](../../Docs/design-implementation.md) 与 [验收](../../Docs/design-implementation-validation.md)。
