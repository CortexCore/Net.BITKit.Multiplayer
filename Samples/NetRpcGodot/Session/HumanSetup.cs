using BITKit.Multiplayer.NetRpc;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer.Samples.NetRpcGodot.Human;

// ④ 只在启动时执行的接线。业务与 Godot 按钮不需要知道这些细节。
public static class HumanSetup
{
    public static ServiceProvider CreateServices(BITKit.Multiplayer.NetRpc.ITransport transport, bool host)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TrainingDummy>();
        services.AddNetRpcObject<DummyActions>();

        if (host) services.AddNetRpcService<IWorkshop, Workshop>();
        else services.AddRemoteInterface<IWorkshop>();

        // 包含契约登记、实体接线和 Host 自动状态发布；不重复 AllowContract/StartSynchronization。
        services.AddNetRpc(host, _ => transport, options: new NetRpcOptions
        {
            SyncInterval = TimeSpan.FromMilliseconds(50),
            SnapshotInterval = TimeSpan.FromSeconds(1)
        });

        var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<RpcContextService>();
            _ = provider.GetRequiredService<TrainingDummy>();
            _ = provider.GetRequiredService<DummyActions>();
            _ = provider.GetRequiredService<IWorkshop>();
            return provider;
        }
        catch { provider.Dispose(); throw; }
    }
}
