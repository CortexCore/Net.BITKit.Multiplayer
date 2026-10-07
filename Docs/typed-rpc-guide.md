# 强类型 RPC 热路径：单向通知、生成调用器与消息租用

> 历史 B6 后端文档：本文实现已删除，不是当前接入方式。当前 API 见 [项目 README](../README.md) 和 [API 契约](api-contracts.md)。

## 本阶段完成的三个部分

1. **void 是单向通知**：不建立应用层结果等待器、超时 CTS 或广播成功 ACK。可靠／不可靠送达仍由选定通道负责。Task / Task<T> 保留执行完成、结果、错误及取消/超时语义。
2. **构建时生成强类型入口和接收器**：现有 Mono.Cecil weaver 直接生成 typed MemoryPack 写入、读取和业务体调用。普通 woven concrete/interface 调用在两种 delivery 下都不经过 object[]、参数装箱或 MethodInfo.Invoke。接收委托在 Bind 时注册一次，不在游戏循环中生成 IL。
3. **一个 RPC 消息的缓冲所有权贯穿发送**：参数直接写进池化 frame，接收器读取长度受限切片，不再每参数创建 byte[]/byte[][]。IRoomMemoryWire 支持借用内存收发，Direct/Relay 与 Arena 适配器均已接入。

这不是 Roslyn Source Generator 项目；本阶段沿用 Cecil 在构建期生成代码，以保留普通带方法体的 RPC 写法。MemoryPack 自身的 DTO Source Generator 继续使用。

## 写法和执行语义

```csharp
[Rpc(SendTo.Host)]
public void SubmitMove(float x, float z, uint sequence)
{
    if (!RpcCallContext.TryGetValue(out var call))
        throw new InvalidOperationException("Missing RPC context");
    // 使用已认证的 call.Sender，不采用调用者自报身份。
    simulation.Move(call.Sender, x, z, sequence);
}

[Rpc(SendTo.Host)]
public Task<int> QueryCount() => Task.FromResult(simulation.Count);
```

生成入口直接将强类型参数写入 TypedRpcWriter；生成接收器从 TypedRpcReader 读出类型值，直接调用保存的原业务体。Host 的 void 本地业务体只执行一次。为冻结广播源参数，当前 wrapper 仍可能在本地体前先编码；没有网络自发自收或反射解码本地 void 参数。

远端 void handler 出错时由**接收端**的 `UnhandledDispatch` 诊断，不再向发送端制造等待中的失败结果。调用方本地参数/角色验证失败仍抛出，异步发送失败仍报告本地诊断。需要知道远端执行结果的业务应使用 Task / Task<T>。

`RpcCallContext.TryGetValue` 返回只读值上下文，无需创建上下文 class。旧 `Current` 继续提供可逃逸的 class 视图，使用它可能分配。值上下文只在同步执行栈内有效；异步 RPC 仍使用正确的 AsyncLocal 上下文。嵌套 async→void→async 的身份、目标和异常恢复已有实际 woven 测试，未使用会被下一次请求覆盖的可变全局上下文。

## 数值头和协议匹配

普通 generated RPC 使用 **B6 / version 4** 的 88 B `readonly struct TypedRpcHeader`：

- kind 1：可靠通知；kind 2：Task 请求；kind 3：不可靠通知。
- scope/target 为冷注册时计算的 128-bit 身份；sender/destination/method 为 64-bit 数值标识。
- schema fingerprint 覆盖参数/返回值、成员顺序、route/delivery、枚举 underlying/value 及 struct layout/packing/size/offset。
- 请求型调用使用值类型 Guid 做关联；通知的请求字段为空值，不生成 GUID 字符串。

头部没有路由字符串。target 哈希的各字段使用长度前缀严格 UTF-8，避免分隔符歧义；活动目录和绑定检查数值碰撞，method ID 碰撞在编织时诊断。指纹用于兼容性校验，不替代身份和权限检查。接收先检查实际通道、route、已认证 sender、target 和本地指纹，再执行 handler。

**当前是每条调用对本地生成描述符的匹配校验，不是 Host 下发完整协议表。** 同房间应使用匹配的构建。动态协议补表、远端-only 方法目录及按需部署仍按计划后置。

## 所有权和容量

- writer 对象从有界 ThreadStatic Stack 复用，字节容量来自 ArrayPool；不通过 ConcurrentStack 每次 Push 新建链表节点。
- wrapper 的 finally 归还未交付缓冲；发送后由 runtime 持有字节租用，所有扇出分支完成/失败后才归还。冷维护的 ready-peer 快照和引用计数扇出避免每次创建收件人数组和 Task[]。
- 同步完成的 ValueTask 也必须消费；异步结果观察保留 frame 直到真实底层写入终止。Dispose 不提前归还仍被 OS/DMTP 引用的内存。
- 每个 runtime 最多 128 个 typed 在途 frame；第 129 个拒绝，不回退为旧通道。UDP 仍是 900 B 应用 payload / 1200 B wire，可靠 frame 上限 1 MiB。
- Relay 的异步 mailbox 在 borrowed callback 返回前复制一次到拥有的池化 frame；转发使用 payload 切片，每个出站 wire frame 的租用活到真实发送完成。不是声称所有网络层之间完全零拷贝。
- variable arrays、strings、reference DTO 的接收拥有自己的值，MemoryPack 可能为它们分配；不能把固定结构体零分配结果外推到任意对象图。

`IRoomMemoryWire` 是正常运行路径。只实现旧 `IRoomWire` 的调用方可通过明确的 owned byte[] 兼容复制继续工作，但那不是零分配路径。MemoryReceived 与 legacy Received 为选定接收路径，runtime 不同时订阅二者来重复处理。

## 兼容性边界

成员目录、SyncVar、移除、显式反射 CreateProxy/CallAsync 和 Task reply 仍保留 **B5/v3** 二进制模型；旧显式 Unreliable 兼容入口可读 **B4/v2**。这些都不是普通 woven RPC 的隐藏 fallback，也没有 JSON 回退。其频率由业务决定，不能因称为控制/兼容路径就推断没有分配。

所有 peers 必须一起重新构建；代码生成后的 DTO 规则沿用 [可靠二进制指南](reliable-binary-guide.md)。构建目标仍是 Core C#9/netstandard2.1，Unity/IL2CPP 尚未实测。

`MeasureUnreliableCall` 仍是显式 MethodInfo/object[] 测量 API，但已缓存固定元数据，不再每个候选计算方法字符串和 target SHA。typed 头固定长度，测量仍使用实际 MemoryPack 编码与同样边界。**调用者创建的新 boxes 不会因缓存而消失**；Arena 现有批次循环仍使用它，后续可再改成生成的专用测量器。

实测和剩余开销见 [typed-rpc-validation.md](typed-rpc-validation.md)。
