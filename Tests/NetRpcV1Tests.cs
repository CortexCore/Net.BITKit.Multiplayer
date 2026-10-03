using Cysharp.Threading.Tasks;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using V1Transport = BITKit.Multiplayer.NetRpc.ITransport;

namespace BITKit.Multiplayer.Tests
{
    public sealed class NetRpcV1Tests
    {
        private sealed class PairTransport : V1Transport
        {
            private PairTransport _peer = null!;
            public event Action<ReadOnlyMemory<byte>>? OnReceived;

            public static (PairTransport A, PairTransport B) Create()
            {
                var a = new PairTransport();
                var b = new PairTransport();
                a._peer = b;
                b._peer = a;
                return (a, b);
            }

            public UniTask Send(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            {
                _peer.OnReceived?.Invoke(payload.ToArray());
                return UniTask.CompletedTask;
            }
            public UniTask SendFast(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Send(payload, cancellationToken);
        }

        private sealed class Calculator
        {
            [Rpc(SendTo.Host)]
            public Task<int> Add(int value) => Task.FromResult(value + 40);

            [Rpc(SendTo.Host)]
            public void Fire(int damage) => LastDamage = damage;

            public int LastDamage { get; private set; }
        }

        public interface IFoo
        {
            Task<int> Plus(int a, int b);
            Task Notify(string message);
        }

        [Fact]
        public void RemoteInterfaceGeneratorEmitsContextModelAndMessagePackWrites()
        {
            var source = RemoteInterfaceSourceGenerator.Generate<IFoo>();

            Assert.Contains("RpcContext _context", source);
            Assert.Contains("NetRpcModel(NetRpcMessageKind.Call", source);
            Assert.Contains("bag.Write<global::System.Int32>(@a)", source);
            Assert.Contains("bag.Write<global::System.Int32>(@b)", source);
            Assert.Contains("_context.RequestTask<", source);
            Assert.Contains("System.Int32", source);
            Assert.Contains("return _context.RequestTask(model)", source);
        }

        [Fact]
        public async Task UnknownTargetRequestsMapThenInvokesDiMethodAndReturnsResult()
        {
            var hostServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            var clientServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            var (clientTransport, hostTransport) = PairTransport.Create();
            using var host = new RpcContextService(hostServices, hostTransport, isServer: true);
            using var client = new RpcContextService(clientServices, clientTransport, isServer: false);

            var context = client.RegisterTarget(7, clientServices.GetRequiredService<Calculator>());
            var method = typeof(Calculator).GetMethod(nameof(Calculator.Add))!;
            var methodId = RpcContextService.RpcMethodId(typeof(Calculator), method);
            using var arguments = new NetMessageBag();
            arguments.Write(2);

            var request = new NetRpcModel(NetRpcMessageKind.Call, 7, methodId, 1, arguments.Count, arguments.Memory);
            var result = await context.SendRequest(request, typeof(int));

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task VoidCallUsesTheSameFrameAndInvokesHostBodyOnce()
        {
            var hostServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            var clientServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            var (clientTransport, hostTransport) = PairTransport.Create();
            using var host = new RpcContextService(hostServices, hostTransport, isServer: true);
            using var client = new RpcContextService(clientServices, clientTransport, isServer: false);

            var context = client.RegisterTarget(9, clientServices.GetRequiredService<Calculator>());
            var method = typeof(Calculator).GetMethod(nameof(Calculator.Fire))!;
            var methodId = RpcContextService.RpcMethodId(typeof(Calculator), method);
            using var arguments = new NetMessageBag();
            arguments.Write(17);

            var call = new NetRpcModel(NetRpcMessageKind.Call, 9, methodId, 0, arguments.Count, arguments.Memory);
            await context.Send(call);

            await Task.Delay(20);
            Assert.Equal(17, hostServices.GetRequiredService<Calculator>().LastDamage);
        }

        [Fact]
        public async Task TcpTransportCarriesTheSameNetRpcV1Call()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            var serverTask = TcpTransport.ListenOnceAsync(new IPEndPoint(IPAddress.Loopback, port));
            await using var clientTransport = await TcpTransport.ConnectAsync("127.0.0.1", port);
            await using var serverTransport = await serverTask;
            var hostServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            var clientServices = new ServiceCollection().AddSingleton<Calculator>().BuildServiceProvider();
            using var host = new RpcContextService(hostServices, serverTransport, isServer: true);
            using var client = new RpcContextService(clientServices, clientTransport, isServer: false);

            var context = client.RegisterTarget(11, clientServices.GetRequiredService<Calculator>());
            var method = typeof(Calculator).GetMethod(nameof(Calculator.Add))!;
            var methodId = RpcContextService.RpcMethodId(typeof(Calculator), method);
            using var arguments = new NetMessageBag();
            arguments.Write(2);

            var request = new NetRpcModel(NetRpcMessageKind.Call, 11, methodId, 3, arguments.Count, arguments.Memory);
            var result = await context.SendRequest(request, typeof(int));

            Assert.Equal(42, result);
        }
    }
}
