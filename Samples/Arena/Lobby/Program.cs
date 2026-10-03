using BITKit.Multiplayer.Samples.Arena;
using System.Net;

try
{
    var port = ArenaProtocol.DefaultLobbyPort;
    string? data = null, ready = null, tlsPfx = null, tlsPasswordEnv = null;
    var bind = "127.0.0.1";
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is not ("--port" or "--data" or "--ready-file" or "--bind" or "--tls-pfx" or "--tls-password-env") || ++i >= args.Length) throw new ArgumentException("Usage: Arena.Lobby --port 17890 [--bind IP] [--data path] [--ready-file path] [--tls-pfx path --tls-password-env VAR]");
        switch (args[i - 1])
        {
            case "--port": if (!int.TryParse(args[i], out port)) throw new ArgumentException("Invalid port"); break;
            case "--data": data = args[i]; break;
            case "--ready-file": ready = args[i]; break;
            case "--bind": bind = args[i]; break;
            case "--tls-pfx": tlsPfx = args[i]; break;
            case "--tls-password-env": tlsPasswordEnv = args[i]; break;
        }
    }
    if (!IPAddress.TryParse(bind, out var bindAddress) || (tlsPfx is null) != (tlsPasswordEnv is null)) throw new ArgumentException("Invalid bind or TLS options");
    var tlsPassword = tlsPasswordEnv is null ? null : Environment.GetEnvironmentVariable(tlsPasswordEnv);
    if (tlsPasswordEnv is not null && tlsPassword is null) throw new ArgumentException("Missing TLS password environment variable");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await using var server = new ArenaLobbyServer(new ArenaLobbyStore(data));
    await server.StartAsync(port, bindAddress, tlsPfx, tlsPassword);
    if (ready is not null)
    {
        var path = Path.GetFullPath(ready);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { Ready = true, Port = port }));
    }
    Console.WriteLine($"Arena Lobby ready on {bindAddress}:{port}");
    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
}
catch (OperationCanceledException) { }
catch (Exception e) { Console.Error.WriteLine($"Arena Lobby startup failed: {e.GetType().Name}"); Environment.ExitCode = 1; }
