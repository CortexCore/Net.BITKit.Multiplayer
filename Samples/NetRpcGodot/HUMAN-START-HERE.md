# 给人类看的 NetRpc 案例：一个商店和一个靶子

这个案例回答一件事：**我能不能像调用普通 C# 服务一样，让真实 Host 执行业务，再在 Godot 看到结果和同步状态？**

## 先运行，再读代码

双击仓库根目录 **`Start-Godot-Human-Lab.cmd`**。

它会构建并启动一个独立 Host 和两个实际 Godot Client。两个窗口连接同一个房间，看到的是同一个公共商店、同一个靶子；不是各自的私有金币/背包。Host 不创建额外的本地 Client。

按下列顺序，只在第一个窗口点按钮，第二个窗口用来观察。重新启动 Host 后，金币/背包为 0，靶子 HP 为 100。

| 操作 | 你应该看到什么 | 对照原始设计 |
| --- | --- | --- |
| 1. `Plus(20, 22)` | 按钮结果显示 42；Host 控制台显示它执行了 Plus | design-v1 v2：生成远程接口，Host 计算并返回 |
| 2. 加 10 金币 | 两个窗口最终都显示 Coins=10；消息列表多一条 | design-v2：接口标量和 IList 同步 |
| 3. 花 3 金币买苹果 | 两个窗口最终都显示 Coins=7、Apples=1；消息多一条 | design-v2：IDictionary 同步 |
| 4. 靶子扣 20 HP | RPC 返回 80；两个窗口最终都显示 HP=80 | design-v1 v3 普通类编织 + design-v2 ECS 同步 |
| 5. Host 广播 Pulse | Host 自己执行一次；每个在线窗口收到一次，显示 Last UDP pulse=1 | design-v1 v4：All、不可靠、void |
| 6. Client 直接写 HP=999 | 显示拒绝；两个窗口 HP 仍为 80 | Host 权威，Client 通过 RPC 请求修改 |

RPC 返回和状态到达是两件事：按钮结果可能先显示 80，HP 标签稍后才更新。它不是“返回以后所有 Client 都已经同步完”。UDP Pulse 是瞬时广播，不是可恢复的同步状态；错过它的后来连接者不会自动补收到旧 Pulse。

## 你先只读这三处

路径都相对于本目录。用 **`NetRpcGodot.sln`** 在 Rider 打开。

1. **`Contracts/IWorkshop.cs`**：声明你想跨网络调用什么、同步什么。
2. **`Game/Workshop.cs`**：Host 真的怎么计算。
3. **`Godot/HumanView.cs` 最前面的业务方法和 `_Process`**：Godot 怎么调用和显示。

先按顺序看这三处即可。`HumanVerification.cs` 是机器验收；生成代码、协议和连接生命周期不需要作为第一课。

### ① 我声明接口

```csharp
public interface IWorkshop
{
    UniTask<int> Plus(int a, int b);
    UniTask<int> AddCoins(int amount);
    UniTask<bool> BuyApple();
    UniTask<int> BroadcastPulse();

    int Coins { get; }
    IList<string> Messages { get; }
    IDictionary<string, int> Inventory { get; }
}
```

方法是 RPC。getter 是自动同步状态：Client 读取 `Coins` 时，是读取已经收到的本地副本，不是发起一次同步网络调用。

### ② Host 写普通业务实现

```csharp
public UniTask<int> Plus(int a, int b)
{
    return UniTask.FromResult(a + b);
}
```

买苹果只做金币扣除、背包更新。Host 的 `Messages` 用 `NetworkList<string>`，`Inventory` 用 `NetworkDictionary<string, int>`；它们通过接口暴露为 IList/IDictionary，容器记录修改并同步。

Host 业务没有 `UdpClient`、序列化、方法 ID、`InvokeAsync`、Unity/Godot Node。

### ③ Client 像普通服务一样调用

启动时从 DI 取到接口后：

```csharp
int answer = await _shop.Plus(20, 22);
bool bought = await _shop.BuyApple();
```

Client 的 `_shop` 是编译出来的原生代理，不是 `Workshop` 实现。真正的计算发生在独立 Host 进程。

Godot 每帧显示已经收到的数据：

```csharp
int coins = _shop.Coins;
bool hasApple = _shop.Inventory.TryGetValue("apple", out int count);
int health = _dummy.Health.Value;
```

网络回复可能从工作线程完成；案例把更新 Godot Label 的动作排到主线程执行。

## 然后看普通类 RPC 和 ECS

打开 **`Game/TrainingDummy.cs`**。

```csharp
public sealed class DummyActions
{
    private readonly TrainingDummy _dummy;

    [Rpc(SendTo.Host)]
    public UniTask<int> Damage(int amount)
        => UniTask.FromResult(_dummy.Damage(amount));
}
```

Client 直接调用 `await _actions.Damage(20)`。它没有本地扣血：实际 Game DLL 在构建期被编织，Client 的调用转发到 Host，Host 执行原业务体。

靶子双方都预先存在：EntityId=1，Health 的 ComponentId=1，初始值 100。组件注册为 `INetComponent`，实体带 `INetworkIdentity`，Runtime 就能按相同身份同步它。这里没有自动生成或销毁 Godot 游戏对象。

```csharp
public NetComponent<int> Health { get; } = new(1, 100);

// 在 Host 的伤害业务中：
Health.Value = Math.Max(0, Health.Value - amount);
```

同步由库发布，不是每个业务自己写 UDP。Client 如果直接赋值会被拒绝。

## 最后才看启动接线

**`Session/HumanSetup.cs`** 是两端启动时共用的 DI 配置：

```csharp
services.AddSingleton<TrainingDummy>();
services.AddSingleton<DummyActions>();

if (host) services.AddNetRpcService<IWorkshop, Workshop>();
else services.AddRemoteInterface<IWorkshop>();

services.AddNetRpc(host, _ => transport, options: syncOptions);
```

- Host 注入真实 `Workshop`，Client 注入生成的 `IWorkshop` 代理。
- `AddSingleton` 按普通 DI 格式注册可编织的 RPC 类。
- `AddNetRpc` 接入传输、契约目录、实体目录；Host 自动启动状态同步。本例检查间隔 50ms、强制快照间隔 1s。
- 连接建立和 socket ownership 仍是启动层责任：Host 在 `Host/HumanHost.cs` 监听；Godot 在 `HumanView` 下方的 region 连接/退出。

**本例不需要手写 `AllowContract`、`StartSynchronization`、TargetKey、手动 Bind 或额外的业务会话接口。** `NetRpc.targets` 使用现有生成器/编织器完成构建；不是引擎内置 Godot RPC。

## 亲手加一个功能

建议第一步添加：

```csharp
// IWorkshop
UniTask<int> Double(int number);

// Workshop
public UniTask<int> Double(int number) => UniTask.FromResult(number * 2);

// HumanView 的一个按钮处理方法
int result = await _shop.Double(21); // 期望 42
```

模仿 `_Ready` 中的 `AddButton` 加一个按钮，关闭旧 Host/Client，然后重新运行 `Start-Godot-Human-Lab.cmd`，它会重建契约、代理和 Host/Client。业务方法不需要自行填写网络 ID 或发包。

## 判断是不是你一开始想要的

- 我声明接口，DI 给 Client 原生代理，Host 提供实现。
- 我写普通业务代码，计算确实在 Host；结果能 await。
- 我给普通类方法加 Rpc 属性，同样的调用写法能转发。
- 我修改 Host 的接口状态/显式网络容器，Client 自动看到副本。
- 我注册带身份的 Entity 和网络组件，纯数据自动同步。
- Godot 只负责输入/显示，不承担网络协议或 Host 业务。

这六点是本例可以直接操作核对的范围。Relay、故障注入、玩家身份/权限、动态对象生命周期等留在进阶/原验收样例，不用在第一屏理解它们。

### 聚焦机器核对（可选）

```powershell
powershell -NoProfile -File Samples/NetRpcGodot/Start-Human-Lab.ps1 -Verify -Headless
```

启动真实独立 Host、一个 Godot Actor 和一个 Godot Observer，检查结果/状态、实际编织、UDP All 及 Client 写拒绝；日志和 summary 在 `Artifacts/NetRpcHuman/<timestamp>/`，结束后清理本轮进程。

### 已实际跑通（2026-10-04）

Human 案例原有 headless 和可见证据使用 Godot 4.6.1 .NET：一个独立 Host + 两个实际进程、原生代理 `NetRemote_3302003033`、实际编织的 `DummyActions`。同一工程的完整同步场已在 Godot 4.7.2 上重新通过 Direct/Relay 双 Client headless 验证。

两端读回：金币 7、苹果 1、消息 2 条、HP 80、UDP Pulse 收到一次、Client 直接写组件被拒绝，两个 Client 正常退出。Host 日志显示 Plus/领取金币/买苹果/扣血确实在 Host 执行。

最新可见证据：`Artifacts/NetRpcHuman/20261004-002416-268/`，包含 `summary.json`、Host/Actor/Observer 日志和两张实际 viewport PNG；已检查中文按钮与两端相同的同步状态。这不是 Unity、Relay 或 Godot 导出包验收。
