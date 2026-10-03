using Cysharp.Threading.Tasks;
using System.Diagnostics;
using BITKit.Multiplayer.NetRpc;
using Mono.Cecil;
using NetRpcPerformance;
using Xunit;

public sealed class GuardTests
{
    [Theory]
    [InlineData("--iterations", "0")]
    [InlineData("--warmup", "0")]
    [InlineData("--payload-size", "65537")]
    [InlineData("--concurrency", "257")]
    [InlineData("--container-size", "4097")]
    [InlineData("--sizes", "1,0")]
    [InlineData("--transport", "fake")]
    [InlineData("--profile", "legacy")]
    [InlineData("--unknown", "1")]
    public void Invalid_configuration_fails_instead_of_silently_changing_workload(string key, string value)
        => Assert.ThrowsAny<ArgumentException>(() => Options.Parse([key, value]));

    [Fact]
    public void Percentiles_use_nearest_rank_and_ignore_unused_warmup_tail()
    {
        long[] values = [30, 10, 20, 999999];
        Assert.Equal(20 * 1000d / Stopwatch.Frequency, Statistics.Percentile(values, 3, .5));
        Assert.Equal(30 * 1000d / Stopwatch.Frequency, Statistics.Percentile(values, 3, .95));
        Assert.Equal(0, Statistics.Percentile(values, 0, .95));
    }

    [Fact]
    public void Actual_generated_and_woven_IL_is_not_a_fake_local_call_or_void_ack()
    {
        Assert.True(Statistics.IsWoven);
        using var module = ModuleDefinition.ReadModule(typeof(Actor).Assembly.Location);
        var actor = module.Types.Single(t => t.Name == "Actor");
        var wrapper = actor.Methods.Single(m => m.Name == "Notify");
        Assert.Contains(wrapper.Body.Instructions, i => i.Operand is MethodReference m && m.DeclaringType.Name == "NetRpcDispatch" && m.Name == "Begin");
        Assert.Contains(wrapper.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "FinishVoid");
        Assert.DoesNotContain(wrapper.Body.Instructions, i => i.Operand is MethodReference m && m.Name.StartsWith("FinishTask"));
        var receiver = actor.Methods.Single(m => m.Name.StartsWith("__netrpc_recv_"));
        Assert.Contains(receiver.Body.Instructions, i => i.Operand is MethodReference m && m.Name.StartsWith("__netrpc_body_"));
        Assert.Contains(receiver.Body.Instructions, i => i.Operand is FieldReference f && f.DeclaringType.Name == "NetRpcResults" && f.Name == "Void");
        var proxy = module.Types.Single(t => t.Name.StartsWith("NetRemote_"));
        Assert.Equal(4, proxy.Methods.Count(m => m.Name.StartsWith("__netrpc_recv_")));
        var scalar = proxy.Methods.Single(m => m.Name == "Scalar");
        Assert.DoesNotContain(scalar.CustomAttributes, a => a.AttributeType.Name == "AsyncStateMachineAttribute");
        Assert.Contains(scalar.Body.Instructions, i => i.Operand is GenericInstanceMethod m &&
            m.DeclaringType.Name == "RpcContext" && m.Name == "Request" && m.GenericArguments.Single().FullName == "System.Int32");
    }

    [Fact]
    public async Task Counting_decorator_counts_only_completed_sends_and_preserves_payload_loan()
    {
        var fake = new ControlledTransport(); var counter = new CountingTransport(fake);
        byte[] bytes = [(byte)NetRpcMessageKind.Component, 2, 3];
        var pending = counter.SendFast(bytes).AsTask();
        Assert.Equal(0, counter.FastSent); Assert.Equal(bytes, fake.Payload.ToArray());
        fake.Complete(); await pending;
        Assert.Equal(1, counter.FastSent); Assert.Equal(3, counter.SentBytes);
        int delivered = 0; counter.OnReceived += data => { Assert.Equal(bytes, data.ToArray()); delivered++; };
        fake.Deliver(bytes);
        Assert.Equal(1, delivered); Assert.Equal(1, counter.ComponentFrames); Assert.Equal(3, counter.ReceivedBytes);
        counter.Reset(); Assert.Equal(0, counter.SentBytes); Assert.Equal(0, counter.ComponentFrames);
    }
    private sealed class ControlledTransport : ITransport
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReadOnlyMemory<byte> Payload;
        public event Action<ReadOnlyMemory<byte>>? OnReceived;
        public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken token = default) { Payload = payload; return _completion.Task.AsUniTask(false); }
        public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken token = default) => Send(payload, token);
        public void Complete() => _completion.SetResult();
        public void Deliver(ReadOnlyMemory<byte> bytes) => OnReceived?.Invoke(bytes);
    }
}
