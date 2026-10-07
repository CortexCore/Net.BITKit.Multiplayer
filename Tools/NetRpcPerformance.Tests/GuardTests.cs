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
    [InlineData("--clients", "0")]
    [InlineData("--clients", "3")]
    [InlineData("--client-id", "2")]
    [InlineData("--rate", "-1")]
    [InlineData("--rate", "10001")]
    [InlineData("--container-size", "4097")]
    [InlineData("--sizes", "1,0")]
    [InlineData("--transport", "fake")]
    [InlineData("--profile", "legacy")]
    [InlineData("--unknown", "1")]
    public void Invalid_configuration_fails_instead_of_silently_changing_workload(string key, string value)
        => Assert.ThrowsAny<ArgumentException>(() => Options.Parse([key, value]));

    [Fact]
    public void Two_clients_are_direct_only_and_rate_is_single_worker_without_delay_pacing()
    {
        Assert.Equal(1, Options.Parse([]).Clients);
        Assert.Equal(2, Options.Parse(["--clients", "2"]).Clients);
        Assert.Equal("client", Options.ClientRole(1, 1));
        Assert.Equal("client-2", Options.ClientRole(2, 2));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--clients", "2", "--transport", "relay"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--clients", "2", "--transport", "both"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--rate", "30", "--concurrency", "2"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--rate", "30", "--pacing-ms", "1"]));
        Assert.Equal(30, Options.Parse(["--rate", "30"]).Rate);
    }

    [Fact]
    public void Every_rpc_client_must_complete_its_own_requests_and_host_must_sum_them()
    {
        var config = new Options { Clients = 2, Profile = "scalar", Iterations = 10, Warmup = 5 };
        NodeReport[] reports = [Report("host", 10001, "scalar", 20) with { ReliableSends = 20 },
            Report("client-1", 10002, "scalar", 10), Report("client-2", 10003, "scalar", 10)];
        Supervisor.Validate(config, reports);
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1] with { Completed = 9 }, reports[2] with { Completed = 11 }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { ReturnFrames = 9 }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0] with { ReliableSends = 10 }, reports[1], reports[2]]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1]]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { Pid = reports[1].Pid }]));
    }

    [Fact]
    public void State_validates_each_client_and_never_infers_udp_delivery_from_host_submissions()
    {
        var config = new Options { Clients = 2, Profile = "component", Iterations = 10, Warmup = 5 };
        NodeReport[] reports = [Report("host", 10001, "component", 10) with { UdpSends = 22 },
            Report("client-1", 10002, "component", 8) with { Callbacks = 8, ComponentFrames = 9 },
            Report("client-2", 10003, "component", 10) with { Callbacks = 10, ComponentFrames = 11 }];
        Supervisor.Validate(config, reports); // Actual loss/duplicate repair frame are allowed.
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { FinalValue = 14 }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { ComponentFrames = null }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { ComponentFrames = 12 }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0] with { UdpSends = 11 }, reports[1], reports[2]]));
        config = config with { Profile = "syncvar" };
        reports = reports.Select(r => r with { Profile = "syncvar", Completed = 10, Callbacks = 10 }).ToArray();
        Supervisor.Validate(config, reports);
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1] with { Callbacks = 9 }, reports[2] with { Callbacks = 11 }]));
    }

    [Fact]
    public void Void_requires_one_fence_per_client_and_counts_all_host_bodies()
    {
        var config = new Options { Clients = 2, Profile = "void", Iterations = 10, Warmup = 5 };
        NodeReport[] reports = [Report("host", 10001, "void", 20) with { ReliableSends = 2, OrdinaryBodyCalls = 30 },
            Report("client-1", 10002, "void", 10) with { ReliableSends = 11, ReturnFrames = 1 },
            Report("client-2", 10003, "void", 10) with { ReliableSends = 11, ReturnFrames = 1 }];
        Supervisor.Validate(config, reports);
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0], reports[1], reports[2] with { ReturnFrames = 2 }]));
        Assert.Throws<InvalidOperationException>(() => Supervisor.Validate(config, [reports[0] with { OrdinaryBodyCalls = 15 }, reports[1], reports[2]]));
    }

    private static NodeReport Report(string role, int pid, string profile, long completed)
        => new(role, pid, profile, 10, 100, 1, 0, 0, 0, completed, 0, 100, 100, 10, 0, 0, 15,
            15, true, 0, 0, "test", role == "host" ? "" : "NetRemote_Test", true, 10, "test", "test", "test",
            1, false, Stopwatch.Frequency, default, default, "test", "test", 0, 0);

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
