using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.LiteNetLibDirect;
using BITKit.Multiplayer.Samples.NetRpcGodot;

int Argument(string name, int fallback) { int index = Array.IndexOf(args, name); return index < 0 ? fallback : int.Parse(args[index + 1]); }
int port = Argument("--port", 28770), relayPort = Argument("--relay-port", 0), seconds = Argument("--seconds", 0);
bool liteNetLib = args.Contains("--litenetlib");
if (liteNetLib && relayPort != 0) throw new ArgumentException("LiteNetLib Relay is not implemented. Use DIRECT only.");
using var lifetime = new CancellationTokenSource(); if (seconds > 0) lifetime.CancelAfter(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
if (args.Contains("--human"))
{
    await HumanHost.Run(Argument("--port", 28810), lifetime.Token);
    return;
}
using var host = new ArenaHost();
using var listener = liteNetLib ? null : new TcpTransportListener(new IPEndPoint(IPAddress.Loopback, port));
await using var liteListener = liteNetLib ? LiteNetLibEndpoint.Listen(port) : null;
var connections = new ConcurrentBag<TcpTransport>(); uint nextPeer = 1;
await using var relay = relayPort == 0 ? null : new RelayHostConnection(host.Runtime, "127.0.0.1", relayPort, "netrpc-godot-lab");
var accept = Accept();
Console.WriteLine("READY " + JsonSerializer.Serialize(new { port = liteNetLib ? liteListener!.LocalPort : listener!.EndPoint.Port, relayPort, pid = Environment.ProcessId, backend = liteNetLib ? "LiteNetLib DIRECT" : "TCP+UDP", authority = "Host only" }));
try
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
    while (await timer.WaitForNextTickAsync(lifetime.Token))
    {
        host.World.Advance(0.02f);
        while (host.Errors.TryDequeue(out var error))
            if (error is not BITKit.Multiplayer.RpcException { Error: BITKit.Multiplayer.RpcError.Unauthorized }) Console.Error.WriteLine(error);
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally
{
    lifetime.Cancel(); listener?.Dispose();
    try { await accept; } catch (Exception) when (lifetime.IsCancellationRequested) { }
    foreach (var connection in connections) await connection.DisposeAsync();
}
async Task Accept()
{
    while (!lifetime.IsCancellationRequested)
    {
        try
        {
            if (liteNetLib) { var connection = await liteListener!.AcceptAsync(lifetime.Token); var peer = ++nextPeer; host.Runtime.AttachPeer(peer, connection); Console.WriteLine("LITENETLIB ATTACH " + peer); connection.Closed += () => Console.WriteLine("LITENETLIB CLOSED " + peer); }
            else { var connection = await listener!.AcceptAsync(lifetime.Token); connections.Add(connection); host.Runtime.AttachPeer(++nextPeer, connection); }
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { break; }
    }
}
