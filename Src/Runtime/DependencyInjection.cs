using System;
using Microsoft.Extensions.DependencyInjection;

namespace BITKit.Multiplayer
{

public static class MultiplayerServiceCollectionExtensions
{
    // Resolve the interface and implementation to exactly the same scoped object.
    // The caller must register an RpcRuntime for this DI scope and dispose it with the scope.
    public static IServiceCollection AddScopedRpcService<TContract, TImplementation>(this IServiceCollection services,
        TargetKey key, Func<AuthorizationRequest, bool>? authorize = null, PeerId? owner = null)
        where TContract : class where TImplementation : class, TContract
    {
        services.AddScoped<TImplementation>(provider =>
        {
            var instance = ActivatorUtilities.CreateInstance<TImplementation>(provider);
            provider.GetRequiredService<RpcRuntime>().Bind(key, instance, authorize, owner);
            return instance;
        });
        services.AddScoped<TContract>(provider => provider.GetRequiredService<TImplementation>());
        return services;
    }
}
}
