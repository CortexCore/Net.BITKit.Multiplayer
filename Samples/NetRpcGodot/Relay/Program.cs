using System.Net;
using BITKit.Multiplayer.NetRpc;

int Argument(string name, int fallback) { int index = Array.IndexOf(args, name); return index < 0 ? fallback : int.Parse(args[index + 1]); }
using var lifetime = new CancellationTokenSource(); int seconds = Argument("--seconds", 0); if (seconds > 0) lifetime.CancelAfter(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
await using var relay = new RelayEndpoint(new IPEndPoint(IPAddress.Loopback, Argument("--port", 28771)), "netrpc-godot-lab");
relay.Faulted += error => Console.Error.WriteLine(error.Message);
Console.WriteLine($"READY Relay port={relay.EndPoint.Port} pid={Environment.ProcessId}");
try { await Task.Delay(Timeout.Infinite, lifetime.Token); } catch (OperationCanceledException) { }
