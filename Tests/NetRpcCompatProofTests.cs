using System.Reflection;
using System.Runtime.Versioning;
using BITKit.Multiplayer.NetRpc;
using Xunit;

namespace BITKit.Multiplayer.Tests;

public sealed class NetRpcCompatProofTests
{
    [Fact]
    public void CompatibilityRunUsesTheNetstandardTransportAssembly() =>
        Assert.Equal(".NETStandard,Version=v2.1", typeof(TcpTransport).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
}
