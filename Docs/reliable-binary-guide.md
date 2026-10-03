# 可靠游戏消息：binary v3 + MemoryPack

**后续普通 woven RPC 已升级为 B6/v4 强类型路径**，两种 delivery 都使用生成的直接处理器，void 不再等待成功回复；v3 继续用于状态/控制/Task reply 和显式兼容调用。当前入口与所有权说明见 [typed-rpc-guide.md](typed-rpc-guide.md)，本页保留 v3 格式和 DTO 规则。

## 责任与范围

原可靠游戏路径中的 JSON/JToken 来自本库 `RpcRuntime`，不是 TouchSocket DMTP 的要求。DMTP 能承载二进制 payload。之前只迁移 Unreliable 参数，没有覆盖高频可靠输入，因此仍有大量 JSON 中间对象。

现在房间内 **call、reply/error、SyncVar state、快照请求、成员目录和目标移除**均使用二进制信封。参数、返回值和状态值使用 MemoryPack 1.21.4。`Src/Runtime` 已无 Newtonsoft/JToken，Core 项目已移除 Newtonsoft 依赖。

Lobby/账号远程 API、一次性 admission 数据及磁盘诊断 JSON 是其他边界；不能把它们与每 Tick 的房间游戏消息混为一谈。该迁移不等于删除程序中所有 JSON 文件或第三方间接依赖。

## 协议兼容性

- 可靠信封：魔数 **0xB5**，版本 **3**，固定字段顺序、有界长度前缀。
- 旧 JSON/v1 可靠帧直接拒绝，**没有自动 JSON 回退**。同一个房间的 Host/Client 必须一起升级；默认启动器会统一重新构建。
- Unreliable 保持 **0xB4 / version 2**，原生 UDP、鉴权、端点绑定和重绑语义保持原样。
- 接收值类型来自已注册方法、待完成调用或 SyncVar 属性，不从网络接受 CLR 类型名。
- Host/All/Target、来源身份、权限、ready barrier、取消/超时、错误、状态版本、迟绑定和墓碑保持原有语义。二进制编码不会把可靠消息变成不可靠消息。

## DTO 声明

标量与 unmanaged 值使用 MemoryPack 内置格式。需要生成器的可靠 DTO 使用默认 `[MemoryPackable] partial`，所有可序列化成员必须显式给出 **从 0 开始连续的 MemoryPackOrder**：

```csharp
using MemoryPack;

[MemoryPackable]
public partial class SpawnInfo
{
    [MemoryPackOrder(0)] public int Id { get; set; }
    [MemoryPackOrder(1)] public string Name { get; set; } = "";
    [MemoryPackOrder(2)] public float X { get; set; }
    [MemoryPackOrder(3)] public float Z { get; set; }
}
```

DTO 项目引用 `MemoryPack.Core` 和 `MemoryPack.Generator` **1.21.4**。先完成生成器编译，再运行 RPC 编织器。Arena 的 PlayerPose、BulletPose、RoomSnapshot 已迁移，并覆盖带 Unicode 字符串的嵌套快照。

可靠参数/返回值支持受限的嵌套 DTO 和一维数组/ArraySegment；编织器和运行时共同限制 schema。当前拒绝循环、多态/union、继承型 DTO、自定义 formatter/callback/构造布局、Include/Ignore 等不受支持的变体。显式顺序避免混合字段/属性时依赖不可靠的反射 token 排序。Unreliable 保持其更严格的扁平 schema 规则。

标量 SyncVar 仍做无集合值的整体替换；显式 SyncList/SyncDictionary/SyncHashSet 已有单独的指纹、快照和增量协议。普通集合、嵌套 DTO 中的集合仍被拒绝。见 [集合与 Hook](sync-collections-guide.md)；本版状态 payload 已升级，参与端需要重新编织，不能混用旧 SyncVar 数据。

## 分配与解码边界

- 可靠帧最多 **1 MiB**，单个 SyncVar 编码值 **16 KiB**，参数最多 **32** 个，数组/segment 每层最多 **256** 项。
- 生成 DTO 最多 254 个成员；schema/value 深度上限 32，递归预检还有 4096 单位的累计预算。成员目录 256、墓碑 512。字符串最多 **65,536 字符**，UTF-8 的声明长度和实际字符数在构造对象前检查。
- `ReliableValues` 的 schema walker 只检查 MemoryPack 布局边界；实际值仍由 MemoryPack 编解码，不是另造动态 DTO 序列化器。
- 有界 `BinaryBufferWriter` 使用 ArrayPool 暂存，在异常和完成时归还。传给现有 `IRoomWire`、待完成返回值和缓冲状态的精确长度 byte[] 是独立拥有的数组，不会在异步发送结束前被归还复用。
- 状态以拥有的编码值缓冲，保留迟绑定和源对象修改隔离；版本与值比较抑制无变化通知。

**二进制不是零分配。** 当前仍有参数 byte[][]/精确长度值数组、帧数组、object[]/装箱、接收字符串、请求 ID、等待器/超时与异步状态机等成本。进一步做低 GC 需要改善这些所有权和调用机制，不能只把 serializer 换个名字。

验证与真实分配变化见 [reliable-binary-validation.md](reliable-binary-validation.md)。此阶段保持 Core C#9/netstandard2.1；生成 DTO 的测试 consumer 使用 net10，不能据此宣称 Unity IL2CPP/source-generator 部署已验证。
