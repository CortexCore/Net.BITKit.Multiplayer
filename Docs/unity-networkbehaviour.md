# Unity NetworkBehaviour（薄适配层）

程序集：`Net.BITKit.Multiplayer.Unity`；命名空间：`BITKit.Multiplayer.Unity`。Unity asmdef 使用者需显式引用该程序集和 Core `Net.BITKit.Multiplayer`。

它只管理一个普通 MonoBehaviour 与现有 RpcRuntime 绑定之间的生命周期，不创建连接、Runtime、计时器或逐帧查询。普通 OOP ECS 组件仍可直接绑定，不要求继承此基类。

```csharp
using BITKit.Multiplayer;
using BITKit.Multiplayer.Unity;
using UnityEngine;

public sealed class MovingPlatform : NetworkBehaviour
{
    [SyncVar] public float Speed { get; private set; } = 1f;

    private void Update()
    {
        if (!IsSpawned || !IsServer) return;
        transform.position += Vector3.right * (Speed * Time.deltaTime);
    }

    protected override void OnStartServer() { /* Host 初始化 */ }
    protected override void OnStartClient() { /* 本组件初始 SyncVar 已应用 */ }
    protected override void OnNetworkDespawn() { /* 释放本地订阅 */ }
}
```

RPC 继续使用 `[Rpc(SendTo.Host/All/Target)]`，SyncVar、Hook 和同步集合继续使用原实现。MonoBehaviour 本身不自动同步 Transform，仍使用实体服务或 NetworkTransform/NetworkRigidbody 的现有位姿通道。

## 状态与回调

- `IsBound`：已关联房间绑定，可能仍在等待初始状态。
- `IsSpawned`：绑定已提交、房间 ready、本组件初始状态已就绪。断线或 Runtime 销毁立即使读取结果为 false。
- `IsServer` / `IsHost`、`IsClient`：当前绑定角色。Host 与 Client 互斥，不采用 Mirror 双角色 Host。
- `OwnerPeerId`：可空的连接身份；`IsOwner` 仅在 IsSpawned 且所有者等于当前 LocalPeerId 时为 true。未分配 owner 的世界对象即使在 Host 上也不是 IsOwner；它仍由 IsServer 决定模拟权威。
- `NetworkId`、`NetworkTarget`：当前对象 ID 与组件目标键。
- `NetworkTime`：所属房间的时间服务，解绑后为 null。
- `OnStartServer` / `OnStartClient`：每次成功绑定至多一次；Client 有 SyncVar 时等待本组件初始快照，无同步字段则在绑定提交后就绪。
- `OnNetworkDespawn`：每次绑定结束一次，也包括等待初始快照时取消；回调时 IsSpawned 已为 false，之后清空角色/ID引用。
- `OnOwnershipChanged(previousOwner, newOwner)`：首次已分配归属在 OnStartServer/Client 后通知，后续变更按 adapter 通知，解绑时释放至 null。回调前 IsOwner 已更新；等待初始快照的绑定不会提前取得所有权或发出获取/释放通知。
- 关闭 Behaviour.enabled 不解绑，也不阻止上述回调。派生类覆盖 OnDestroy 时必须调用 base.OnDestroy。

Core 的 `Synchronized`、`TargetUnbound` 和 `LifetimeCancellation` 提供通知；生命周期事件无每帧轮询。已排队的生命周期动作带绑定代次，旧租约 Dispose 不影响新绑定。回调异常记录为 Unity exception，状态已提交，不回滚网络状态。

## Project B 接入

`UnityNetworkEntitiesService` / `GameRpcObjectBindings` 会发现 Identity 子树中的 NetworkBehaviour（含 disabled/inactive 子组件），跳过嵌套 Identity 的所有权边界；目标键由相对名称路径、具体类型和同类型序号组成。两端需使用匹配内容，不支持绑定后任意重排组件。

组件通过 `IGameRpcSession.BindEntity` 使用已有主线程 wire。实体服务默认状态公开可读、RPC 只允许已分配的 owner Client；未分配 owner 时拒绝 Client RPC。业务可以使用实体服务的 `AuthorizeRpc` 明确覆盖调用/状态可见性策略。

如果使用者自己搭建 adapter，可调用 `behaviour.BindNetwork(runtime, targetKey, networkId, authorize, owner, ownership)` 并持有其返回的 IDisposable。`ownership` 是 Core 的只读 `INetworkOwnership`（OwnerPeerId + OwnershipChanged）；adapter 负责经认证的复制与主线程通知。未提供该 source 时，使用现有 owner 参数作为本次绑定的固定归属提示，但它不会自动跨网络复制。

**对象 Ownership 与 `RpcRuntime.Bind(..., owner:)` 不同**：后者还会限制状态接收人，适合私有库存，不能直接用于所有人都应看见的角色。Project B 使用已有可靠实体 roster 的 NGO3 快照/增量复制归属，为同一实体的各组件提供共享只读 source，RPC 按 Host 当前 handle 的归属校验；不把该归属传成私有状态过滤参数。Core 本次只增加只读接口，未增加另一套 Core ownership 消息。

绑定/解绑必须在 Unity 主线程；网络消息必须通过主线程 wire 执行业务体、原始状态 setter 和 Hook。基类的生命周期 marshal 不会把整个 Core 接收路径自动变成 Unity 主线程。

绑定失败、Target Remove、world 租约释放、Host 离场和 Runtime Dispose 会结束组件生命周期。对未曾激活的对象，Unity 未必调用 OnDestroy，因此持有者仍必须释放绑定租约，不能只依赖 MonoBehaviour 回调。

Project B 的 Host 通过 `IUnityNetworkEntitiesService.SetOwner(networkId, peerOrNull)` 分配/转移/释放，或 SpawnAsync 的可选 owner 参数指定初始归属。只能分配给本房间 ready peer；离开/降级会清空归属，不自动销毁对象。变化同值无通知，旧 PeerId 不自动映射到重连账号。所有权不赋予 Client 直接写 SyncVar 或模拟 Host 物理的权限。

没有 IsLocalPlayer 的隐含推导（拥有的物体未必是玩家本体）、预测回滚、自动字段同步或全局 active Runtime。

## NetworkObjectEvents

Add Component：**BITKit > Networking > Network Object Events**。

- **Host Only**：拖入只由 Host 执行的 Behaviour，Host ready 时启用，Client 及等待身份/初始状态时禁用。
- **Disable Until Bound**：默认开启，在 Awake 即暂停列表，避免网络就绪前执行 Update。离线本地对象若不进入网络绑定，应关闭该选项或不挂此组件。
- **On Host Ready / On Client Ready / On Network Despawn**：原生 UnityEvent，都是本地通知，不额外发网络事件。
- 解绑恢复修改前的 enabled 状态；重复目标去重，不会禁用自身。代码配置可在绑定前调用 `ConfigureHostOnly(...)`。
- 缓存只在绑定/配置时建立；没有自己的 Update/ticker。Awake 中不可执行需要网络权威的副作用，禁用 Behaviour 不能撤销已执行的 Awake。
- 不要把网络传输、位姿接收器或其他必须在 Client 运行的基础设施拖进 Host Only。

## 验证（2026-09-29）

- Core .NET **99/99** 通过，分配 benchmark 按设计跳过；新增状态就绪、解绑通知和 Runtime lifetime 回归。
- Unity 2022.3.41f1 MCP 编译成功。
- 实际运行程序集中的 NetworkBehaviourProbe 由 Unity ILPP 编织。真实 Observation Join/sidecar 的 Game Session Smoke 返回 Passed=true、Error为空：
  - BehaviourLifecycle=true：Host/Client 回调各一次、互斥角色、disabled 组件仍收到回调、Client 回调读到初值17、回调主线程。
  - BehaviourRpc=true：Client Add(4) 只执行 Host 业务体，双方 SyncVar 最终21。
  - BehaviourRoleEvents=true / BehaviourCleanup=true：Host Only 启停、UnityEvent 次数、解绑后原 enabled 状态恢复。
  - BehaviourWaitsForSnapshot=true / BehaviourPendingCleanup=true：没有初始快照不冒充 spawned，取消等待正确清理。
  - BehaviourOldLeaseSafe=true / BehaviourTargetRemoval=true / BehaviourRuntimeDisposed=true。
- 场景最终 dirty=False、playing=False。测试组件放在 Game/Probes 运行时程序集；Unity 不允许将 Editor 程序集的 MonoBehaviour 添加到 GameObject。

尚未做 Player/IL2CPP、跨机器、完整 Play 或此基类的专门内存计量；没有将“无自身逐帧工作”声明为整个网络零 GC。

### Ownership 增量验证（2026-09-29）

- Core 回归仍为 **99 通过 / 1 benchmark 跳过**，netstandard2.1 编译通过；Unity MCP 无编译错误。
- Game Session Smoke 升为真实 Host + owner Client + late observer Client，Passed=true、Error为空。新增十项 Ownership 标志全部 true：初始归属/主线程、Client 分配拒绝、未知 peer 拒绝、迟加入非 owner 可见状态、非 owner RPC 拒绝、旧 owner 立即拒绝、Host 转移、Client 转移、同值无重复、离线释放。
- 原 RPC/集合/实体/主线程/world 清理检查继续通过。pending snapshot 不提前 IsOwner；旧 lease、target retirement 清理归属。
- 宿主新增 3 个纯 roster EditMode 测试 + 1 个加载期间变更测试，**4/4** 通过；覆盖 UTF-8 peer、历史不被改写、旧版本/重复/缺口、原子拒绝、旧协议/截断拒绝、迟到加载和 Clear。
- 另单项回归原动态 Spawn→roster→UDP motion→Despawn 通过；本轮定向 EditMode 共 **5/5**。
- 最终 map_prototype dirty=False、playing=False；证据是 Edit Mode Direct 会话，不是完整玩家输入/动画、Relay 三端或 Player/AOT 验收。
