using System.Net;
using System.Text.Json;
using BITKit.Multiplayer.Samples.Arena;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;

try
{
    int port = ArenaProtocol.DefaultRelayPort, lobbyPort = ArenaProtocol.DefaultLobbyPort;
    string lobbyAddress = "127.0.0.1", bind = "127.0.0.1";
    string transport = "native";
    string? report = null, ready = null, pfx = null, passwordEnvironment = null, lobbyTlsTarget = null, udpControlFile = null;
    var lobbyTls = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--lobby-tls") { lobbyTls = true; continue; }
        if (args[i] is not ("--port" or "--lobby-address" or "--lobby-port" or "--lobby-tls-target" or "--bind" or "--report" or "--ready-file" or "--tls-pfx" or "--tls-password-env" or "--udp-control-file" or "--transport") || ++i >= args.Length)
            throw new ArgumentException("Invalid Relay arguments");
        switch (args[i - 1])
        {
            case "--port": if (!int.TryParse(args[i], out port)) throw new ArgumentException("Invalid port"); break;
            case "--lobby-port": if (!int.TryParse(args[i], out lobbyPort)) throw new ArgumentException("Invalid lobby port"); break;
            case "--lobby-address": lobbyAddress = args[i]; break;
            case "--lobby-tls-target": lobbyTlsTarget = args[i]; break;
            case "--bind": bind = args[i]; break;
            case "--report": report = args[i]; break;
            case "--ready-file": ready = args[i]; break;
            case "--tls-pfx": pfx = args[i]; break;
            case "--tls-password-env": passwordEnvironment = args[i]; break;
            case "--udp-control-file": udpControlFile = Path.GetFullPath(args[i]); break;
            case "--transport": transport = args[i]; break;
        }
    }
    if (port is < 1 or > 65535 || lobbyPort is < 1 or > 65535 ||
        !IPAddress.TryParse(bind, out var bindAddress) ||
         (pfx is null) != (passwordEnvironment is null) || (lobbyTlsTarget is not null && !lobbyTls) ||
         transport is not ("native" or "touchsocket")) throw new ArgumentException("Invalid Relay endpoint, TLS or transport options");
    var password = passwordEnvironment is null ? null : Environment.GetEnvironmentVariable(passwordEnvironment);
    if (passwordEnvironment is not null && password is null) throw new ArgumentException("TLS password environment variable unavailable");
    var services = new ServiceCollection();
    if (transport == "touchsocket") services.AddSingleton<ITransportFactory, TouchSocketUdpTransportFactory>();
    services.AddUdpTransport();
    using var provider = services.BuildServiceProvider();
    ITransportFactory transportFactory = provider.GetRequiredService<ITransportFactory>();

    // A failed lobby connection must prevent readiness and return a nonzero exit code.
    await using var lobby = await ArenaLobbyClient.ConnectAsync(lobbyAddress, lobbyPort, lobbyTls, lobbyTlsTarget);
    using var relay = new TouchSocketRelayServer(new ArenaRelayAuthorizer(lobby), transportFactory);
    // Whitelist phases, never echo transport exception messages (they can contain credentials).
    relay.Diagnostic += message => Console.Error.WriteLine(message switch
    {
        var m when m.StartsWith("Relay handshake rejected:", StringComparison.Ordinal) => "Relay handshake rejected",
        var m when m.StartsWith("Relay register rejected:", StringComparison.Ordinal) => "Relay host registration rejected",
        var m when m.StartsWith("Relay select rejected:", StringComparison.Ordinal) => "Relay client selection rejected",
        var m when m.StartsWith("Relay session rejected:", StringComparison.Ordinal) => "Relay session ended with error",
        _ => "Relay connection event"
    });
    await relay.StartAsync(new RelayListenOptions
    {
        Port = port, BindAddress = bindAddress, VerifyToken = ArenaProtocol.RelayToken,
        CertificatePath = pfx, CertificatePassword = password
    });
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    if (ready is not null) await ArenaAtomicFile.WriteAsync(ready, JsonSerializer.Serialize(new { Ready = true, Port = port, TransportName = transportFactory.Name }), stop.Token);
    Console.WriteLine($"Arena Relay ready on {bindAddress}:{port} ({transportFactory.Name})");
    try
    {
        while (!stop.IsCancellationRequested)
        {
            if (udpControlFile != null && File.Exists(udpControlFile))
            {
                try
                {
                    string command = (await File.ReadAllTextAsync(udpControlFile, stop.Token)).Trim();
                    if (command == "on") relay.UdpForwardingEnabled = true;
                    else if (command == "off") relay.UdpForwardingEnabled = false;
                }
                catch (IOException) { /* A concurrent administrator write is retried on the next poll. */ }
            }
            if (report is not null) await ArenaAtomicFile.WriteAsync(report, JsonSerializer.Serialize(relay.GetStatistics()), stop.Token);
            await Task.Delay(250, stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
catch (Exception e)
{
    // No exception messages: transport errors may embed endpoints or credentials.
    Console.Error.WriteLine($"Arena Relay failed: {e.GetType().Name}");
    Environment.ExitCode = 1;
}
