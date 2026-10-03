using System.Globalization;
using System.Text.Json;
using BITKit.Multiplayer.Samples.Arena;
using BITKit.Multiplayer;
using BITKit.Multiplayer.TouchSocket;
using BITKit.Multiplayer.Transport;
using Microsoft.Extensions.DependencyInjection;
using Raylib_cs;

namespace BITKit.Multiplayer.Samples.Arena.App;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        AppOptions? options = null;
        CancellationTokenSource? cancellation = null;
        ArenaReport report = new();
        string stage = "arguments";
        try
        {
            options = AppOptions.Parse(args);
            report.Role = options.Role;
            var services = new ServiceCollection();
            if (options.Transport == "touchsocket") services.AddSingleton<ITransportFactory, TouchSocketUdpTransportFactory>();
            services.AddUdpTransport();
            using var provider = services.BuildServiceProvider();
            ITransportFactory transportFactory = provider.GetRequiredService<ITransportFactory>();
            using var stop = new CancellationTokenSource();
            cancellation = stop;
            ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                stage = "lobby connection";
                Console.WriteLine($"Connecting to lobby at {options.LobbyAddress}:{options.LobbyPort}...");
                await using IArenaLobbyApi lobby = await ArenaLobbyClient.ConnectAsync(
                    options.LobbyAddress, options.LobbyPort, options.LobbyTls, options.LobbyTlsTarget, cancellation.Token);

                stage = "authentication";
                AuthSession auth = options.Auth switch
                {
                    "register" => await lobby.RegisterAsync(options.Username, options.Password),
                    "login" => await lobby.LoginAsync(options.Username, options.Password),
                    _ => await lobby.GuestAsync(options.Name)
                };
                if (!auth.Success) throw new InvalidOperationException("Authentication rejected: " + SafeError(auth.Error, options));
                options.SessionToken = auth.SessionToken;
                report.PlayerId = auth.PlayerId;
                Console.WriteLine($"Authenticated as {auth.DisplayName} ({Short(auth.PlayerId)}).");

                stage = "arena session";
                IArenaSession active;
                if (options.Role == "host")
                {
                    active = await ArenaSession.StartHostAsync(lobby, auth, new ArenaHostOptions
                    {
                        RoomName = options.RoomName,
                        AdvertisedAddress = options.AdvertisedAddress,
                        Port = options.GamePort,
                        LocalPlayer = !options.NoLocalPlayer,
                        ConnectionMode = options.HostConnectionMode,
                        RelayAddress = options.RelayAddress,
                        RelayPort = options.RelayPort,
                        RelayUseTls = options.RelayTls
                    }, cancellation.Token, transportFactory);
                }
                else
                {
                    string roomId = options.RoomId;
                    if (roomId.Length == 0)
                    {
                        RoomInfo[] rooms = await lobby.ListRoomsAsync(auth.SessionToken);
                        if (rooms.Length == 0) throw new InvalidOperationException("No available rooms.");
                        roomId = rooms[0].RoomId;
                    }
                    active = await ArenaSession.StartClientAsync(lobby, auth,
                        new ArenaClientOptions { RoomId = roomId }, cancellation.Token, transportFactory);
                }

                await using (active)
                {
                    report.PlayerId = active.LocalPlayerId;
                    report.PeerId = active.LocalPeerId;
                    report.RoomId = active.RoomId;
                    report.ConnectionMode = active.ConnectionMode;
                    report.ConnectedEndpoint = active.ConnectedEndpoint;
                    report.TransportName = active.TransportName;
                    Console.WriteLine($"{options.Role} joined room {active.RoomName} ({Short(active.RoomId)}), player {Short(active.LocalPlayerId)}, peer {Short(active.LocalPeerId)}.");
                    stage = "arena running";
                    if (options.AllocationPhases) report.Allocations = new ArenaAllocationReport();
                    var controller = new ArenaController(active, options, report);
                    if (options.Headless)
                        await RunHeadlessAsync(controller, cancellation.Token);
                    else
                        RunWindow(controller, cancellation.Token); // all Raylib operations stay on this one thread
                }
                // Disposal includes the acknowledged leave. Keep teardown failures in the final report.
                if (active.LastError.Length > 0) report.Error = SafeError(active.LastError, options);
                report.IsReady = active.IsReady;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                if (options.Report.Length != 0) WriteJsonAtomically(options.Report, report);
            }
            Console.WriteLine("Arena session closed.");
            return report.Error.Length == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            if (options?.Report.Length > 0) WriteJsonAtomically(options.Report, report);
            return 0;
        }
        catch (Exception ex)
        {
            // Never print exception messages here: transport exceptions can embed credentials/tickets.
            string error = (ex is ArgumentException && stage == "arguments") ||
                           (ex is TimeoutException && ex.Message == "UDP binding did not become ready within 8 seconds") ||
                           (ex is InvalidOperationException &&
                           (ex.Message.StartsWith("Authentication rejected:", StringComparison.Ordinal) ||
                            ex.Message == "No available rooms."))
                ? ex.Message : $"{stage} failed ({ex.GetType().Name})";
            report.Error = options is null ? error : SafeError(error, options);
            Console.Error.WriteLine(report.Error);
            if (options?.Report.Length > 0)
            {
                try { WriteJsonAtomically(options.Report, report); }
                catch (Exception writeError) { Console.Error.WriteLine($"Could not write error report ({writeError.GetType().Name})."); }
            }
            return 1;
        }
    }

    private static async Task RunHeadlessAsync(ArenaController controller, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && !controller.Expired)
        {
            controller.Update(false);
            try { await Task.Delay(16, cancellation); }
            catch (OperationCanceledException) { break; }
        }
        controller.Finish();
    }

    private static void RunWindow(ArenaController controller, CancellationToken cancellation)
    {
        bool opened = false;
        try
        {
            Raylib.InitWindow(ArenaRenderer.Width, ArenaRenderer.Height, "BITKit  /  ARENA V2");
            opened = true;
            if (controller.Options.WindowX.HasValue || controller.Options.WindowY.HasValue)
            {
                var position = Raylib.GetWindowPosition();
                Raylib.SetWindowPosition(controller.Options.WindowX ?? (int)position.X,
                    controller.Options.WindowY ?? (int)position.Y);
            }
            Raylib.SetTargetFPS(60);
            var renderer = new ArenaRenderer(controller.Options.View == "3d");
            int framesAfterReady = 0;
            bool captured = false;
            while (!cancellation.IsCancellationRequested && !controller.Expired && !Raylib.WindowShouldClose())
            {
                if (Raylib.IsKeyPressed(KeyboardKey.F2)) renderer.ToggleView();
                if (Raylib.IsKeyPressed(KeyboardKey.F3)) controller.ToggleInterpolation();
                if (Raylib.IsKeyPressed(KeyboardKey.F4)) controller.ToggleUdp();
                if (Raylib.IsKeyPressed(KeyboardKey.F5)) controller.RebindUdp();
                controller.Update(true, renderer);
                if (controller.Report.WasReady) framesAfterReady++;
                string? capturePath = !captured && framesAfterReady >= 120 && controller.Options.Capture.Length > 0
                    ? Path.GetFullPath(controller.Options.Capture) : null;
                if (capturePath != null) EnsureParent(capturePath);
                renderer.Draw(controller, capturePath);
                if (capturePath != null)
                {
                    Console.WriteLine($"Captured {controller.Options.Capture}");
                    captured = true;
                }
            }
            controller.Finish();
        }
        finally
        {
            if (opened) Raylib.CloseWindow();
        }
    }

    internal static string Short(string id) => id.Length <= 10 ? id : id[..10];

    internal static string SafeError(string error, AppOptions options)
    {
        foreach (string secret in new[] { options.Password, options.SessionToken })
            if (secret.Length > 0) error = error.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return error;
    }

    internal static void EnsureParent(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    }

    internal static void WriteJsonAtomically<T>(string path, T value)
        => ArenaAtomicFile.Write(path, JsonSerializer.Serialize(value, JsonOptions));
}

internal sealed class AppOptions
{
    internal string Role { get; private set; } = "";
    internal string LobbyAddress { get; private set; } = "127.0.0.1";
    internal int LobbyPort { get; private set; } = ArenaProtocol.DefaultLobbyPort;
    internal bool LobbyTls { get; private set; }
    internal string? LobbyTlsTarget { get; private set; }
    internal string Name { get; private set; } = "Player";
    internal string Auth { get; private set; } = "guest";
    internal string Username { get; private set; } = "";
    internal string Password { get; private set; } = "";
    internal string SessionToken { get; set; } = "";
    internal string RoomName { get; private set; } = "ArenaV2";
    internal string RoomId { get; private set; } = "";
    internal string AdvertisedAddress { get; private set; } = "127.0.0.1";
    internal int GamePort { get; private set; } = ArenaProtocol.DefaultGamePort;
    internal string Route { get; private set; } = "auto";
    internal string RelayAddress { get; private set; } = "127.0.0.1";
    internal int RelayPort { get; private set; } = ArenaProtocol.DefaultRelayPort;
    internal bool RelayTls { get; private set; }
    private bool HostRouteSpecified { get; set; }
    internal ArenaConnectionMode HostConnectionMode => Route == "relay" || (Route == "auto" && !NoLocalPlayer)
        ? ArenaConnectionMode.Relay : ArenaConnectionMode.Direct;
    internal bool Headless { get; private set; }
    internal bool AllocationPhases { get; private set; }
    internal string Transport { get; private set; } = "native";
    internal bool NoLocalPlayer { get; private set; }
    internal double Duration { get; private set; }
    internal string Bot { get; private set; } = "idle";
    internal string Report { get; private set; } = "";
    internal string ReadyFile { get; private set; } = "";
    internal string Capture { get; private set; } = "";
    internal string View { get; private set; } = "2d";
    internal int? WindowX { get; private set; }
    internal int? WindowY { get; private set; }
    internal int InterpolationMilliseconds { get; private set; } = 100;
    internal bool InterpolationEnabled { get; private set; } = true;
    internal int PoseDelayMilliseconds { get; private set; }
    internal int PoseJitterMilliseconds { get; private set; }
    internal double? UdpOffAfter { get; private set; }
    internal double? UdpOnAfter { get; private set; }
    internal double? UdpRebindAfter { get; private set; }

    internal static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key is "--headless" or "--no-local-player" or "--no-interpolation" or "--relay-tls" or "--lobby-tls" or "--allocation-phases")
            {
                if (key == "--headless") o.Headless = true;
                else if (key == "--allocation-phases") o.AllocationPhases = true;
                else if (key == "--no-local-player") o.NoLocalPlayer = true;
                else if (key == "--relay-tls") { o.RelayTls = true; o.HostRouteSpecified = true; }
                else if (key == "--lobby-tls") o.LobbyTls = true;
                else o.InterpolationEnabled = false;
                continue;
            }
            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unexpected positional argument. Options use --key value.");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {KnownValueOption(key)}.");
            string value = args[++i];
            if (value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {KnownValueOption(key)}.");
            switch (key)
            {
                case "--role": o.Role = value; break;
                case "--lobby-address": o.LobbyAddress = value; break;
                case "--lobby-port": o.LobbyPort = ParsePort(value, key); break;
                case "--lobby-tls-target": o.LobbyTlsTarget = value; break;
                case "--name": o.Name = value; break;
                case "--auth": o.Auth = value; break;
                case "--username": o.Username = value; break;
                case "--password": o.Password = value; break;
                case "--room": o.RoomId = value; break;
                case "--room-name": o.RoomName = value; break;
                case "--advertised-address": o.AdvertisedAddress = value; break;
                case "--game-port": o.GamePort = ParsePort(value, key); break;
                case "--route": o.Route = value; o.HostRouteSpecified = true; break;
                case "--transport": o.Transport = value; break;
                case "--relay-address": o.RelayAddress = value; o.HostRouteSpecified = true; break;
                case "--relay-port": o.RelayPort = ParsePort(value, key); o.HostRouteSpecified = true; break;
                case "--duration":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) ||
                        duration < 0 || !double.IsFinite(duration))
                        throw new ArgumentException("--duration must be a finite number of seconds >= 0.");
                    o.Duration = duration;
                    break;
                case "--bot": o.Bot = value; break;
                case "--report": o.Report = value; break;
                case "--ready-file": o.ReadyFile = value; break;
                case "--view": o.View = value; break;
                case "--capture": o.Capture = value; break;
                case "--window-x": o.WindowX = ParseCoordinate(value, key); break;
                case "--window-y": o.WindowY = ParseCoordinate(value, key); break;
                case "--interpolation-ms": o.InterpolationMilliseconds = ParseRange(value, key, 500); break;
                case "--pose-delay-ms": o.PoseDelayMilliseconds = ParseRange(value, key, 1000); break;
                case "--pose-jitter-ms": o.PoseJitterMilliseconds = ParseRange(value, key, 500); break;
                case "--udp-off-after": o.UdpOffAfter = ParseSeconds(value, key); break;
                case "--udp-on-after": o.UdpOnAfter = ParseSeconds(value, key); break;
                case "--udp-rebind-after": o.UdpRebindAfter = ParseSeconds(value, key); break;
                default: throw new ArgumentException("Unknown option. Expected --role host|client and documented --key value options.");
            }
        }
        if (o.Role is not ("host" or "client")) throw new ArgumentException("--role must be host or client.");
        if (o.Route is not ("auto" or "direct" or "relay")) throw new ArgumentException("--route must be auto, direct or relay.");
        if (o.Transport is not ("native" or "touchsocket")) throw new ArgumentException("--transport must be native or touchsocket.");
        if (o.Role != "host" && o.HostRouteSpecified)
            throw new ArgumentException("--route and --relay-* are host-only; clients follow the lobby descriptor.");
        if (o.RelayAddress.Length == 0) throw new ArgumentException("--relay-address cannot be empty.");
        if (o.Auth is not ("guest" or "register" or "login")) throw new ArgumentException("--auth must be guest, register or login.");
        if (o.Bot is not ("idle" or "move" or "shoot")) throw new ArgumentException("--bot must be idle, move or shoot.");
        if (o.View is not ("2d" or "3d")) throw new ArgumentException("--view must be 2d or 3d.");
        if (o.Name.Length == 0) throw new ArgumentException("--name cannot be empty.");
        if (o.LobbyAddress.Length == 0) throw new ArgumentException("--lobby-address cannot be empty.");
        if (o.LobbyTlsTarget != null && (!o.LobbyTls || o.LobbyTlsTarget.Length == 0))
            throw new ArgumentException("--lobby-tls-target requires --lobby-tls and a nonempty hostname.");
        if (o.RoomName.Length == 0) throw new ArgumentException("--room-name cannot be empty.");
        if (o.Auth != "guest" && (o.Username.Length == 0 || o.Password.Length == 0))
            throw new ArgumentException("--auth register/login requires --username and --password.");
        if (o.NoLocalPlayer && o.Role != "host") throw new ArgumentException("--no-local-player is only valid for --role host.");
        if (o.UdpRebindAfter.HasValue && o.Role != "client") throw new ArgumentException("--udp-rebind-after is client-only.");
        return o;
    }

    private static string KnownValueOption(string key) => key is
        "--role" or "--lobby-address" or "--lobby-port" or "--lobby-tls-target" or "--name" or "--auth" or
        "--username" or "--password" or "--room" or "--room-name" or "--advertised-address" or
        "--game-port" or "--route" or "--transport" or "--relay-address" or "--relay-port" or "--duration" or "--bot" or "--report" or "--ready-file" or
        "--view" or "--capture" or "--window-x" or "--window-y" or
        "--interpolation-ms" or "--pose-delay-ms" or "--pose-jitter-ms" or "--udp-off-after" or "--udp-on-after" or "--udp-rebind-after" ? key : "unknown option";

    private static double ParseSeconds(string value, string key)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds >= 0)
            return seconds;
        throw new ArgumentException($"{key} must be a finite number of seconds >= 0.");
    }

    private static int ParseRange(string value, string name, int maximum)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0 && number <= maximum)
            return number;
        throw new ArgumentException($"{name} must be an integer from 0 to {maximum}.");
    }

    private static int ParsePort(string value, string name)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535)
            return port;
        throw new ArgumentException($"{name} must be an integer from 1 to 65535.");
    }

    private static int ParseCoordinate(string value, string name)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int coordinate)) return coordinate;
        throw new ArgumentException($"{name} must be an integer pixel coordinate.");
    }
}
