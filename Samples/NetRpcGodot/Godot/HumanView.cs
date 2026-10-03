using System.Collections.Concurrent;
using BITKit.Multiplayer.NetRpc;
using BITKit.Multiplayer.Samples.NetRpcGodot.Human;
using Cysharp.Threading.Tasks;
using Godot;
using Microsoft.Extensions.DependencyInjection;

public partial class HumanView : Control
{
    private IWorkshop? _shop;
    private DummyActions? _actions;
    private TrainingDummy? _dummy;
    private Label _result = null!, _state = null!;

    // ⑤ 你在 Godot 写的消费者代码：调用普通接口，读取同步到本地的状态。
    private async UniTask Plus()
    {
        int answer = await _shop!.Plus(20, 22);
        ShowResult($"Host 算出了 {answer}，预期是 42。计算不在客户端执行。");
    }

    private async UniTask EarnCoins()
    {
        int coins = await _shop!.AddCoins(10);
        ShowResult($"Host 返回金币总数 {coins}。两个窗口会自动显示这个状态。");
    }

    private async UniTask BuyApple()
    {
        bool bought = await _shop!.BuyApple();
        ShowResult(bought ? "Host 同意购买：扣 3 金币，背包增加一个苹果。" : "Host 拒绝购买：金币不足。");
    }

    private async UniTask Damage()
    {
        int health = await _actions!.Damage(20);
        ShowResult($"Host 扣血后返回 {health} HP。靶子组件稍后同步到两个窗口。");
    }

    private async UniTask Pulse()
    {
        int pulse = await _shop!.BroadcastPulse();
        ShowResult($"Host 发出了第 {pulse} 次提示。它走 UDP；发出成功不等于保证送达。");
    }

    private UniTask TryLocalWrite()
    {
        try { _dummy!.Health.Value = 999; ShowResult("错误：客户端竟然直接改血成功了！"); }
        catch (BITKit.Multiplayer.RpcException) { ShowResult("符合预期：客户端不能直接改血量，要通过 RPC 请求 Host 修改。"); }
        return UniTask.CompletedTask;
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < 64 && _main.TryDequeue(out var action); i++) action();
        if (_shop == null || _dummy == null) return;
        int apples = _shop.Inventory.TryGetValue("apple", out int count) ? count : 0;
        _state.Text = $"金币：{_shop.Coins}    背包里的苹果：{apples}\n" +
            $"靶子血量：{_dummy.Health.Value}    组件同步版本：{_dummy.Health.Revision}\n" +
            $"最新提示编号：{_dummy.LastPulse}    本端收到提示次数：{_dummy.PulseCount}\n\n" +
            "自动同步的消息列表：\n" + string.Join("\n", _shop.Messages);
    }

    #region Godot UI and connection lifetime — read this after the business example
    private readonly ConcurrentQueue<Action> _main = new();
    private readonly object _handoff = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<ServiceProvider> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServiceProvider? _services;
    private TcpTransport? _socket;
    private bool _exiting;

    public override void _Ready()
    {
        var column = new VBoxContainer { Position = new Vector2(40, 30), Size = new Vector2(1050, 680) };
        column.AddThemeConstantOverride("separation", 12); AddChild(column);
        var title = new Label { Text = "NetRpc：小商店与靶子" }; title.AddThemeFontSizeOverride("font_size", 28); column.AddChild(title);
        column.AddChild(new Label { Text = "两个窗口共享 Host 的商店和靶子。在一个窗口操作，在另一个窗口观察变化。" });
        AddButton(column, "1. 让 Host 计算 20 + 22（远程接口）", Plus);
        AddButton(column, "2. 领取 10 金币（数值和消息列表自动同步）", EarnCoins);
        AddButton(column, "3. 花 3 金币买一个苹果（背包字典自动同步）", BuyApple);
        AddButton(column, "4. 打靶子，扣 20 血（普通类 RPC + ECS 组件同步）", Damage);
        AddButton(column, "5. 请 Host 给所有端发一次提示（UDP 广播）", Pulse);
        AddButton(column, "6. 尝试在客户端把血量改为 999（应当失败）", TryLocalWrite);
        _result = new Label { Text = "正在连接 Host……", AutowrapMode = TextServer.AutowrapMode.WordSmart }; column.AddChild(_result);
        _state = new Label(); column.AddChild(_state);
        var arguments = OS.GetCmdlineUserArgs(); int port = Argument(arguments, "--port", 28810);
        GetWindow().Title = "NetRpc Human Example / " + GetArgument(arguments, "--name", "Client");
        Connect(port).Forget();
        if (arguments.Contains("--human-verify") || arguments.Contains("--human-observe")) Verify(arguments.Contains("--human-observe")).Forget();
    }

    private void AddButton(VBoxContainer parent, string text, Func<UniTask> operation)
    {
        var button = new Button { Text = text };
        button.Pressed += () => Run(operation).Forget(); parent.AddChild(button);
    }
    private async UniTask Run(Func<UniTask> operation)
    {
        if (_shop == null) { ShowResult("请先等待 Host 连接完成。"); return; }
        try { await operation(); } catch (Exception error) { ShowResult(error.GetBaseException().Message); }
    }
    private bool Post(Action action)
    { lock (_handoff) { if (_exiting) return false; _main.Enqueue(action); return true; } }
    private void ShowResult(string text) => Post(() => _result.Text = text);

    private async UniTask Connect(int port)
    {
        TcpTransport? socket = null; ServiceProvider? services = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            socket = await TcpTransport.ConnectAsync("127.0.0.1", port, cancellationToken: timeout.Token);
            services = HumanSetup.CreateServices(socket, host: false);
            var ownedSocket = socket; var ownedServices = services;
            if (!Post(() =>
            {
                if (_exiting) { Close(ownedServices, ownedSocket).Forget(); return; }
                _socket = ownedSocket; _services = ownedServices;
                _shop = ownedServices.GetRequiredService<IWorkshop>();
                _actions = ownedServices.GetRequiredService<DummyActions>();
                _dummy = ownedServices.GetRequiredService<TrainingDummy>();
                _result.Text = "已连接。先点按钮 1 看返回值，再领取金币和买苹果。";
                _ready.TrySetResult(ownedServices); GD.Print("HUMAN_READY");
            })) await Close(ownedServices, ownedSocket);
        }
        catch (Exception error)
        {
            if (services != null) services.Dispose(); if (socket != null) await socket.DisposeAsync();
            _ready.TrySetException(error); ShowResult(error.GetBaseException().Message);
        }
    }
    private async UniTask Verify(bool observer)
    {
        try
        {
            var services = await _ready.Task;
            string result = await HumanVerification.Run(services, observer, _lifetime.Token);
            GD.Print("HUMAN_PASS " + result);
            string capture = GetArgument(OS.GetCmdlineUserArgs(), "--capture-human", "");
            if (capture.Length == 0) Post(() => GetTree().Quit());
            else Post(() => CaptureAndQuit(capture).Forget());
        }
        catch (Exception error) { GD.PrintErr("HUMAN_FAIL " + error); Post(() => GetTree().Quit(1)); }
    }
    private async UniTask CaptureAndQuit(string path)
    {
        try
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var error = GetViewport().GetTexture().GetImage().SavePng(path);
            if (error != Error.Ok) throw new IOException("Viewport capture failed: " + error);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PrintErr("HUMAN_FAIL " + error); GetTree().Quit(1); }
    }
    private static async UniTask Close(ServiceProvider? services, TcpTransport? socket)
    { services?.Dispose(); if (socket != null) await socket.DisposeAsync(); }
    public override void _ExitTree()
    {
        lock (_handoff) _exiting = true;
        _lifetime.Cancel(); _ready.TrySetCanceled(_lifetime.Token);
        while (_main.TryDequeue(out var action)) action();
        Close(_services, _socket).Forget();
    }
    private static int Argument(string[] args, string key, int fallback) => int.Parse(GetArgument(args, key, fallback.ToString()));
    private static string GetArgument(string[] args, string key, string fallback)
    { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
    #endregion
}
