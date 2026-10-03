using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BITKit.Multiplayer.Transport
{
    public sealed class UdpTransportFactory : ITransportFactory
    {
        public string Name => "native-udp";
        public ITransport Create() => new UdpTransport();
    }

    public static class UdpTransportServiceCollectionExtensions
    {
        public static IServiceCollection AddUdpTransport(this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            services.TryAddSingleton<ITransportFactory, UdpTransportFactory>();
            return services;
        }
    }
}
