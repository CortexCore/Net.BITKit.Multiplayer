using System.Numerics;
using BITKit.Multiplayer.Samples.Arena;
using Raylib_cs;

namespace BITKit.Multiplayer.Samples.Arena.App;

internal sealed class ArenaRenderer
{
    internal const int Width = 1200;
    internal const int Height = 800;
    private const int Left = 68;
    private const int Top = 105;
    private const int Size = 640;
    private const int Sidebar = 790;
    private static readonly Color Back = new(11, 20, 29, 255);
    private static readonly Color Panel = new(20, 34, 45, 255);
    private static readonly Color Grid = new(38, 65, 72, 255);
    private static readonly Color Ink = new(220, 235, 229, 255);
    private static readonly Color Muted = new(132, 166, 168, 255);
    private static readonly Color Accent = new(65, 216, 171, 255);
    private static readonly Color Enemy = new(255, 135, 98, 255);
    private static readonly Color Gold = new(250, 211, 110, 255);
    private static readonly Camera3D Camera = new()
    {
        // Shift the world left in the full-window camera projection so the arena
        // is centered in its clipped playfield rather than underneath the HUD.
        Position = new Vector3(23.2f, 31, 16.6f), Target = new Vector3(5.2f, 0, -4.4f),
        Up = Vector3.UnitY, FovY = 44, Projection = CameraProjection.Perspective
    };
    private bool threeD;

    internal ArenaRenderer(bool threeD = false) => this.threeD = threeD;
    internal void ToggleView() => threeD = !threeD;
    internal bool MouseInsideArena()
    {
        Vector2 p = Raylib.GetMousePosition();
        return p.X >= Left && p.X <= Left + Size && p.Y >= Top && p.Y <= Top + Size;
    }

    internal Vector2 MouseWorld()
    {
        Vector2 mouse = Raylib.GetMousePosition();
        if (!threeD)
            return new Vector2((mouse.X - Left) / Size * 24 - 12, (mouse.Y - Top) / Size * 24 - 12);
        Ray ray = Raylib.GetScreenToWorldRay(mouse, Camera);
        if (MathF.Abs(ray.Direction.Y) < 0.0001f) return Vector2.Zero;
        float t = -ray.Position.Y / ray.Direction.Y;
        return t > 0 ? new Vector2(ray.Position.X + ray.Direction.X * t, ray.Position.Z + ray.Direction.Z * t) : Vector2.Zero;
    }

    internal void Draw(ArenaController controller, string? capturePath = null)
    {
        RoomSnapshot view = controller.RenderView;
        Raylib.BeginDrawing();
        try
        {
            Raylib.ClearBackground(Back);
            Raylib.DrawRectangle(0, 0, Width, 70, Panel);
            Raylib.DrawRectangle(18, 17, 5, 36, Accent);
            Text("B / ARENA", 38, 19, 27, Ink);
            Text("V2  /  LIVE MULTIPLAYER", 234, 28, 15, Muted);
            Text(threeD ? "03 / TACTICAL 3D" : "02 / TACTICAL 2D", 548, 28, 17, Accent);
            Raylib.DrawRectangle(Sidebar - 18, 78, 1, 694, Grid);
            Raylib.DrawRectangle(Left - 13, Top - 13, Size + 26, Size + 26, Panel);
            if (threeD) Draw3D(view, controller.PlayerId);
            else Draw2D(view, controller.PlayerId);
            DrawHud(controller);
            if (capturePath != null)
            {
                // TakeScreenshot prepends the working directory even for Windows absolute paths.
                Rlgl.DrawRenderBatchActive();
                var image = Raylib.LoadImageFromScreen();
                try
                {
                    if (!Raylib.ExportImage(image, capturePath))
                        throw new IOException("Could not export arena capture.");
                }
                finally { Raylib.UnloadImage(image); }
            }
        }
        finally { Raylib.EndDrawing(); }
    }

    private static void Draw2D(RoomSnapshot view, string ownId)
    {
        Raylib.DrawRectangle(Left, Top, Size, Size, new Color(17, 40, 47, 255));
        for (int i = 0; i <= 12; i++)
        {
            int p = i * Size / 12;
            Raylib.DrawLine(Left + p, Top, Left + p, Top + Size, Grid);
            Raylib.DrawLine(Left, Top + p, Left + Size, Top + p, Grid);
        }
        Raylib.DrawRectangleLines(Left, Top, Size, Size, Accent);
        Raylib.DrawCircle(Left + Size / 2, Top + Size / 2, 44, new Color(34, 77, 76, 255));
        Raylib.DrawCircleLines(Left + Size / 2, Top + Size / 2, 44, Grid);
        foreach (BulletPose bullet in view.Bullets)
        {
            Vector2 p = ToPixel(bullet.X, bullet.Z);
            Raylib.DrawCircleV(p, 5, Gold);
        }
        foreach (PlayerPose p in view.Players)
        {
            Vector2 pos = ToPixel(p.X, p.Z);
            Color color = p.Health <= 0 ? Muted : p.PlayerId == ownId ? Accent : Enemy;
            Raylib.DrawCircleV(pos, 17, new Color(7, 19, 24, 255));
            Raylib.DrawCircleV(pos, 13, color);
            Raylib.DrawCircleLines((int)pos.X, (int)pos.Y, 19, color);
            Raylib.DrawLine((int)pos.X, (int)pos.Y, (int)(pos.X + p.AimX * 25), (int)(pos.Y + p.AimZ * 25), Ink);
            DrawNameAndHealth(p, (int)pos.X, (int)pos.Y - 32, color);
        }
        Text("-12", Left + 8, Top + Size - 24, 13, Muted);
        Text("+12", Left + Size - 40, Top + Size - 24, 13, Muted);
    }

    private static void Draw3D(RoomSnapshot view, string ownId)
    {
        Raylib.BeginScissorMode(Left, Top, Size, Size);
        try
        {
            Raylib.DrawRectangle(Left, Top, Size, Size, new Color(18, 42, 50, 255));
            Raylib.BeginMode3D(Camera);
            try
            {
                Raylib.DrawCube(new Vector3(0, -0.25f, 0), 24, 0.4f, 24, new Color(24, 57, 59, 255));
                for (int i = -12; i <= 12; i += 2)
                {
                    Raylib.DrawLine3D(new Vector3(i, 0.01f, -12), new Vector3(i, 0.01f, 12), Grid);
                    Raylib.DrawLine3D(new Vector3(-12, 0.01f, i), new Vector3(12, 0.01f, i), Grid);
                }
                foreach (BulletPose b in view.Bullets)
                    Raylib.DrawSphere(new Vector3(b.X, 0.65f, b.Z), 0.19f, Gold);
                foreach (PlayerPose p in view.Players)
                {
                    Color c = p.Health <= 0 ? Muted : p.PlayerId == ownId ? Accent : Enemy;
                    Vector3 pos = new(p.X, 0.65f, p.Z);
                    Raylib.DrawCube(pos, 0.8f, 1.3f, 0.8f, c);
                    Raylib.DrawSphere(new Vector3(p.X, 1.55f, p.Z), 0.39f, c);
                    Raylib.DrawLine3D(new Vector3(p.X, 0.8f, p.Z), new Vector3(p.X + p.AimX, 0.8f, p.Z + p.AimZ), Ink);
                }
            }
            finally { Raylib.EndMode3D(); }
            foreach (PlayerPose p in view.Players)
            {
                Vector2 position = Raylib.GetWorldToScreen(new Vector3(p.X, 2.25f, p.Z), Camera);
                if (position.X >= Left && position.X < Left + Size && position.Y >= Top && position.Y < Top + Size)
                    DrawNameAndHealth(p, (int)position.X, (int)position.Y, p.PlayerId == ownId ? Accent : Enemy);
            }
        }
        finally { Raylib.EndScissorMode(); }
        Raylib.DrawRectangleLines(Left, Top, Size, Size, Accent);
    }

    private static Vector2 ToPixel(float x, float z) =>
        new(Left + (x + 12) / 24 * Size, Top + (z + 12) / 24 * Size);

    private static void DrawNameAndHealth(PlayerPose p, int x, int y, Color color)
    {
        x = Math.Clamp(x, Left + 48, Left + Size - 48);
        y = Math.Clamp(y, Top + 18, Top + Size - 5);
        string name = p.Name.Length > 18 ? p.Name[..18] : p.Name;
        Text(name, x - 48, y - 18, 14, Ink);
        Raylib.DrawRectangle(x - 48, y, 96, 5, Back);
        Raylib.DrawRectangle(x - 48, y, (int)(96 * Math.Clamp(p.Health / 100f, 0, 1)), 5, color);
    }

    private static void DrawHud(ArenaController c)
    {
        RoomSnapshot v = c.View;
        int x = Sidebar + 12;
        Text("OPERATIONS / STATUS", x, 100, 17, Accent);
        Raylib.DrawCircle(x + 5, 142, 4, c.Ready ? Accent : Gold);
        Text(c.Ready ? "CONNECTED / READY" : "SYNCHRONIZING", x + 18, 133, 17, c.Ready ? Accent : Gold);
        Text($"ROLE    {c.Options.Role.ToUpperInvariant()}", x, 174, 17, Ink);
        Text($"ROOM    {Trim(c.RoomName, 22)}", x, 201, 16, Ink);
        Text($"ID      {Program.Short(c.RoomId)}", x, 225, 15, Muted);
        Text($"PEER    {Program.Short(c.PeerId)}", x, 249, 15, Muted);
        Text($"PLAYER  {Program.Short(c.PlayerId)}", x, 273, 15, Muted);
        Text($"ROUTE   {c.ConnectionMode.ToString().ToUpperInvariant()}", x, 293, 13, Accent);
        Text($"LINK    {Trim(c.ConnectedEndpoint, 32)}", x, 310, 13, Muted);
        Raylib.DrawLine(x, 333, Width - 24, 333, Grid);
        Text("LIVE TELEMETRY", x, 341, 17, Accent);
        Text($"TICK          {v.Tick}", x, 361, 17, Ink);
        Text($"OPERATORS     {v.Players.Length}", x, 387, 17, Ink);
        Text($"SHOTS         {v.TotalShots}", x, 413, 17, Ink);
        Text($"HITS          {v.TotalHits}", x, 439, 17, Ink);
        PlayerPose? own = v.Players.FirstOrDefault(p => p.PlayerId == c.PlayerId);
        Text(own == null ? "HP            --" : $"HP            {own.Health:000}", x, 465, 17, own?.Health > 25 ? Accent : Enemy);
        ArenaPresentationReport pose = c.Report.Presentation;
        Text($"POSE {(pose.Enabled ? "INTERP" : "DIRECT")}  {pose.BufferMilliseconds}MS  T{pose.RenderTick:0.0} / {pose.BufferedSnapshots}", x, 480, 13, Gold);
        var udp = c.Report.Datagrams;
        Text($"UDP {(udp.Enabled ? "ON" : "OFF")}/{(udp.Ready ? "BOUND" : "WAIT")}  T{udp.PositionTick}  RX{udp.ReceivedDatagrams}", x, 645, 13, Gold);
        Raylib.DrawLine(x, 500, Width - 24, 500, Grid);
        Text("SQUAD / LIVE", x, 513, 17, Accent);
        int y = 543;
        foreach (PlayerPose p in v.Players.Take(5))
        {
            Color color = p.PlayerId == c.PlayerId ? Accent : Enemy;
            Raylib.DrawCircle(x + 6, y + 8, 5, color);
            Text($"{Trim(p.Name, 17)}  {p.Health}HP", x + 19, y, 15, Ink);
            y += 27;
        }
        Raylib.DrawLine(x, 686, Width - 24, 686, Grid);
        Text("WASD MOVE  /  MOUSE AIM", x, 698, 14, Muted);
        Text($"POSE DELAY/JITTER {pose.SimulatedDelayMilliseconds}/{pose.SimulatedJitterMilliseconds}MS", x, 666, 13, Gold);
        Text("LMB FIRE / F2 VIEW / F3 POSE", x, 718, 13, Muted);
        Text("F4 UDP / F5 REBIND (CLIENT)", x, 754, 13, Muted);
        Text("ESC QUIT", x, 738, 14, Muted);
        Text(c.Options.Bot != "idle" ? $"AUTOPILOT / {c.Options.Bot.ToUpperInvariant()}" : "MANUAL CONTROL", Left, 760, 15, Gold);
        if (c.Report.Error.Length != 0) Text("LINK ERROR: " + Trim(c.Report.Error, 32), x, 76, 13, Enemy);
    }

    private static string Trim(string text, int limit) => text.Length <= limit ? text : text[..limit] + "...";
    private static void Text(string value, int x, int y, int size, Color color) => Raylib.DrawText(value, x, y, size, color);
}
