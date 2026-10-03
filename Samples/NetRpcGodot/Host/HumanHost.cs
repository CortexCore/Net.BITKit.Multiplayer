using System.Net;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.Samples.NetRpcGodot.Human;
using Microsoft.Extensions.DependencyInjection;

// 启动文件：一个独立 Host 接受多个 Client，共享同一商店和靶子。
public static class HumanHost
{
    public static async Task Run(int port, CancellationToken token)
    {
        using var listener = new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, port));
        var sockets = new List<TcpTransport>();
        ServiceProvider? services = null;
        uint nextPeer = 1;
        Console.WriteLine($"[HOST] Human example listening at 127.0.0.1:{listener.EndPoint.Port}; PID {Environment.ProcessId}");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(token);
                sockets.Add(socket);
                if (services == null)
                {
                    services = HumanSetup.CreateServices(socket, host: true);
                    services.GetRequiredService<RpcContextService>().Faulted += error => Console.Error.WriteLine(error);
                    nextPeer = 2; // AddNetRpc 已接入第一个 Client。
                }
                else services.GetRequiredService<RpcContextService>().AttachPeer(++nextPeer, socket);
                Console.WriteLine($"[HOST] Client peer {nextPeer} connected");
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally
        {
            services?.Dispose();
            foreach (var socket in sockets) await socket.DisposeAsync();
        }
    }
}
