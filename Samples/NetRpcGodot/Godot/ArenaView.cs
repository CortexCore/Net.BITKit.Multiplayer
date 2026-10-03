using System.Collections.Concurrent;
using System.Text.Json;
using Cysharp.Threading.Tasks;
using BITKit.Multiplayer.Samples.NetRpcGodot;
using Godot;
using Environment = System.Environment;

public partial class ArenaView : Node2D
{
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ArenaClient? _client;
    private readonly NetworkImpairment _impairment = new();
    private LineEdit _address = null!, _name = null!;
    private SpinBox _port = null!, _latency = null!, _loss = null!;
    private OptionButton _slot = null!;
    private CheckBox _reorder = null!;
    private Label _status = null!, _stats = null!, _events = null!;
    private string _message = "Choose Direct 28770 / Relay 28771, then connect (LiteNetLib: --litenetlib DIRECT).";
    private double _inputTime;
    private int _mainThreadId;
    private bool _connecting, _auto;
    private bool _captured;
    private readonly Vector2[] _rendered = { new(200, 250), new(340, 250) };
    private string[] _arguments = Array.Empty<string>();
    private bool LiteNetLib => _arguments.Contains("--litenetlib");
    private static readonly Color Cyan = new(0.2f, 0.86f, 0.92f), Amber = new(1, 0.67f, 0.28f), Muted = new(0.48f, 0.58f, 0.67f);
    public override void _Ready()
    {
        _mainThreadId = Environment.CurrentManagedThreadId; _arguments = OS.GetCmdlineUserArgs(); _auto = _arguments.Contains("--auto");
        BuildUi(); if (_auto) RunAutomation(); else if (_arguments.Contains("--connect")) Connect();
    }
    private string Argument(string key, string fallback) { int index = Array.IndexOf(_arguments, key); return index < 0 ? fallback : _arguments[index + 1]; }
    private void BuildUi()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore }; AddChild(root);
        var title = new Label { Text = "BITKIT  /  NETRPC SYNC LAB", Position = new Vector2(32, 18) }; title.AddThemeFontSizeOverride("font_size", 28); root.AddChild(title);
        var subtitle = new Label { Text = (LiteNetLib ? "LiteNetLib DIRECT" : "Native TCP + UDP") + "  |  Host authority  |  Generated interface + woven C# RPC", Position = new Vector2(34, 58) }; subtitle.Modulate = Muted; root.AddChild(subtitle);
        var connection = new HBoxContainer { Position = new Vector2(32, 94), Size = new Vector2(1090, 36) }; root.AddChild(connection);
        _address = new LineEdit { Text = "127.0.0.1", CustomMinimumSize = new Vector2(150, 34) }; connection.AddChild(_address);
        _port = new SpinBox { MinValue = 1, MaxValue = 65535, Value = int.Parse(Argument("--port", "28770")), CustomMinimumSize = new Vector2(105, 34) }; connection.AddChild(_port);
        int slot = int.Parse(Argument("--slot", "1")); _name = new LineEdit { Text = "Player " + slot, CustomMinimumSize = new Vector2(155, 34), MaxLength = 24 }; connection.AddChild(_name);
        _slot = new OptionButton(); _slot.AddItem("Player 1", 1); _slot.AddItem("Player 2", 2); _slot.Selected = slot - 1; connection.AddChild(_slot);
        Button connect = new() { Text = "CONNECT" }; connect.Pressed += Connect; connection.AddChild(connect);
        Button disconnect = new() { Text = "DISCONNECT" }; disconnect.Pressed += Disconnect; connection.AddChild(disconnect);
        _status = new Label { Position = new Vector2(34, 140), Size = new Vector2(1070, 25) }; root.AddChild(_status);
        var panel = new VBoxContainer { Position = new Vector2(900, 184), Size = new Vector2(224, 480) }; root.AddChild(panel);
        panel.AddChild(new Label { Text = "NETWORK CONDITIONS" });
        panel.AddChild(new Label { Text = "Latency / ms" }); _latency = new SpinBox { MinValue = 0, MaxValue = 500, Step = 10 }; panel.AddChild(_latency);
        panel.AddChild(new Label { Text = "UDP loss / %" }); _loss = new SpinBox { MinValue = 0, MaxValue = 100, Step = 5 }; panel.AddChild(_loss);
        _reorder = new CheckBox { Text = "Reorder UDP snapshots" }; panel.AddChild(_reorder);
        _latency.ValueChanged += value => _impairment.LatencyMs = (int)value; _loss.ValueChanged += value => _impairment.LossPercent = (int)value; _reorder.Toggled += value => _impairment.Reorder = value;
        Button gap = new() { Text = "DROP NEXT EVENT DELTA" }; gap.Pressed += () => _impairment.DropNextEventDelta = true; panel.AddChild(gap);
        Button attack = new() { Text = "ATTACK OTHER  [SPACE]" }; attack.Pressed += Attack; panel.AddChild(attack);
        Button pickup = new() { Text = "PICKUP NEAREST  [E]" }; pickup.Pressed += Pickup; panel.AddChild(pickup);
        Button respawn = new() { Text = "RESPAWN  [R]" }; respawn.Pressed += Respawn; panel.AddChild(respawn);
        _stats = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; panel.AddChild(_stats);
        _events = new Label { Position = new Vector2(34, 690), Size = new Vector2(1090, 55) }; _events.Modulate = Muted; root.AddChild(_events);
        GetWindow().Title = "NetRpc Sync Lab - Player " + slot;
    }
    public override void _Process(double delta)
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId) throw new InvalidOperationException("Godot presentation is not on its main thread.");
        for (int i = 0; i < 32 && _mainThread.TryDequeue(out var action); i++) action();
        if (_client is { Connected: true } client)
        {
            for (int i = 0; i < 2; i++)
            {
                var p = client.World.Players[i].Position.Value; var target = new Vector2(p.X, p.Y);
                _rendered[i] = _rendered[i].DistanceTo(target) > 150 ? target : _rendered[i].Lerp(target, (float)Math.Min(1, delta * 14));
            }
            if (_auto) { _latency.SetValueNoSignal(client.Impairment.LatencyMs); _loss.SetValueNoSignal(client.Impairment.LossPercent); _reorder.SetPressedNoSignal(client.Impairment.Reorder); }
            _inputTime += delta;
            if (!_auto && _inputTime >= 0.1)
            {
                _inputTime = 0; var focus = GetViewport().GuiGetFocusOwner();
                float x = 0, y = 0;
                if (focus is not LineEdit && focus is not SpinBox)
                { x = (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0); y = (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0); }
                client.Remote.Input(client.Slot, x, y);
            }
            _stats.Text = $"Player {client.Slot}  |  tick {client.Remote.Tick}\n" +
                $"Woven combat: {client.UsesWovenCombat}\n" +
                $"P1 score: {(client.Remote.Scores.TryGetValue(1, out var score) ? score : 0)}\n" +
                $"Inventory entries: {client.Remote.Inventory.Count}\n" +
                $"UDP dropped: {client.Impairment.DroppedDatagrams}\nUDP reordered: {client.Impairment.ReorderedDatagrams}\nDelta gaps: {client.Impairment.DroppedDeltas}\n" +
                $"Position rev: {client.World.Players[client.Slot - 1].Position.Revision}";
            _events.Text = string.Join("  |  ", client.Remote.Events.TakeLast(3));
            while (client.Errors.TryDequeue(out var error)) _message = error.Message;
            if (!_captured && client.Remote.ConnectedPlayers.Count == 2 && client.World.Players[1].Health.Value == 80 && client.Remote.Inventory.ContainsKey(101) && _arguments.Contains("--capture")) { _captured = true; Capture(); }
        }
        else _stats.Text = "No active connection.\n\nWASD: move\nSPACE: attack\nE: pickup\nR: respawn\n\nHost controls all state.";
        _status.Text = _message; QueueRedraw();
    }
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || _auto) return;
        if (key.PhysicalKeycode == Key.Space) Attack(); else if (key.PhysicalKeycode == Key.E) Pickup(); else if (key.PhysicalKeycode == Key.R) Respawn();
    }
    public override void _Draw()
    {
        var origin = new Vector2(34, 180); var scale = new Vector2(1.05f, 1);
        DrawRect(new Rect2(origin, new Vector2(840, 500)), new Color(0.055f, 0.078f, 0.11f));
        for (int x = 0; x <= 800; x += 40) DrawLine(origin + new Vector2(x * scale.X, 0), origin + new Vector2(x * scale.X, 500), new Color(0.10f, 0.14f, 0.18f));
        for (int y = 0; y <= 500; y += 40) DrawLine(origin + new Vector2(0, y), origin + new Vector2(840, y), new Color(0.10f, 0.14f, 0.18f));
        var font = ThemeDB.FallbackFont;
        foreach (int pickup in _client?.Remote.AvailablePickups.ToArray() ?? new[] { 1, 2, 3 })
        { var p = ArenaRules.PickupPosition(pickup); var at = origin + new Vector2(p.X, p.Y) * scale; DrawCircle(at, 9, new Color(0.61f, 0.94f, 0.43f)); DrawString(font, at + new Vector2(15, 5), "ITEM " + pickup, modulate: Muted, fontSize: 14); }
        for (int i = 0; i < 2; i++)
        {
            var player = _client?.World.Players[i]; var p = player?.Position.Value ?? ArenaRules.Spawn(i + 1); int health = player?.Health.Value ?? 100;
            bool connected = _client?.Remote.ConnectedPlayers.Contains(i + 1) == true;
            var at = origin + (_client == null ? new Vector2(p.X, p.Y) : _rendered[i]) * scale; var color = connected ? i == 0 ? Cyan : Amber : Muted;
            DrawRect(new Rect2(at - new Vector2(13, 13), new Vector2(26, 26)), color);
            if (_client?.Slot == i + 1) DrawArc(at, 22, 0, Mathf.Tau, 40, color, 2);
            DrawRect(new Rect2(at + new Vector2(-25, -30), new Vector2(50, 5)), new Color(0.17f, 0.2f, 0.24f));
            DrawRect(new Rect2(at + new Vector2(-25, -30), new Vector2(health * 0.5f, 5)), color);
            DrawString(font, at + new Vector2(-28, -40), $"P{i + 1} / {health} HP", fontSize: 14, modulate: color);
        }
    }
    private void Connect() => ConnectAsync().Forget();
    private void Disconnect() => DisconnectAsync().Forget();
    private void Attack() => AttackAsync().Forget();
    private void Pickup() => PickupAsync().Forget();
    private void Respawn() => RespawnAsync().Forget();
    private void RunAutomation() => RunAutomationAsync().Forget();
    private void Capture() => CaptureAsync().Forget();
    private async UniTask ConnectAsync()
    {
        if (_connecting || _client != null) return;
        _connecting = true; _message = "Connecting..."; string address = _address.Text, name = _name.Text; int port = (int)_port.Value, slot = _slot.GetSelectedId();
        try { var client = await ArenaClient.ConnectAsync(address, port, name, slot, _impairment, _lifetime.Token, LiteNetLib); _mainThread.Enqueue(() => { _client = client; _message = $"Connected as P{client.Slot}. WASD to move."; _connecting = false; }); }
        catch (Exception error) { _mainThread.Enqueue(() => { _message = error.Message; _connecting = false; }); }
    }
    private async UniTask DisconnectAsync() { var client = _client; _client = null; if (client != null) await client.DisposeUniTaskAsync(); _mainThread.Enqueue(() => _message = "Disconnected."); }
    private async UniTask AttackAsync() { var c = _client; if (c == null) return; try { int health = await c.Combat.Attack(c.Slot, c.Slot == 1 ? 2 : 1); _mainThread.Enqueue(() => _message = $"RPC completed: target {health} HP. Snapshot is a separate completion."); } catch (Exception e) { _mainThread.Enqueue(() => _message = e.Message); } }
    private async UniTask PickupAsync() { var c = _client; if (c == null) return; try { int id = c.Remote.AvailablePickups.OrderBy(id => ArenaWorld.Distance(c.World.Players[c.Slot - 1].Position.Value, ArenaRules.PickupPosition(id))).FirstOrDefault(); bool picked = await c.Remote.Pickup(c.Slot, id); _mainThread.Enqueue(() => _message = picked ? "Pickup accepted by Host." : "Move closer to a pickup."); } catch (Exception e) { _mainThread.Enqueue(() => _message = e.Message); } }
    private async UniTask RespawnAsync() { var c = _client; if (c == null) return; try { await c.Remote.Respawn(c.Slot); } catch (Exception e) { _mainThread.Enqueue(() => _message = e.Message); } }
    private async UniTask RunAutomationAsync()
    {
        int slot = int.Parse(Argument("--slot", "1")), port = int.Parse(Argument("--port", "28770")); string resultPath = Argument("--result", "client-result.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
         var proof = await ArenaAutomation.Run(() => ArenaClient.ConnectAsync("127.0.0.1", port, "Auto P" + slot, slot, cancellationToken: timeout.Token, liteNetLib: LiteNetLib), slot,
            c => _mainThread.Enqueue(() => { _client = c; _message = c == null ? "Reconnecting / finishing..." : "Automated cross-process verification running."; }), timeout.Token);
        proof.RealGodot = true;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(resultPath))!);
        await System.IO.File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        _mainThread.Enqueue(() => { GD.Print(proof.Passed ? "NETRPC_GODOT_PASS " + slot : "NETRPC_GODOT_FAIL " + proof.Error); GetTree().Quit(proof.Passed ? 0 : 1); });
    }
    private async UniTask CaptureAsync()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = Argument("--capture", "capture.png"); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        GetViewport().GetTexture().GetImage().SavePng(path);
    }
    public override void _ExitTree() { _lifetime.Cancel(); var client = _client; _client = null; if (client != null) client.DisposeUniTaskAsync().Forget(); }
}
