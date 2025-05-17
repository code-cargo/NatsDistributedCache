using CodeCargo.Nats.DistributedCache;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace CodeCargo.Nats.HybridCache;

/// <summary>
/// Extension methods for setting up NATS hybrid cache related services in an <see cref="IServiceCollection" />.
/// </summary>
public static class NatsHybridCacheExtensions
{
    public static IHybridCacheBuilder AddNatsHybridCache(
        this IServiceCollection services,
        Action<NatsCacheOptions> configureOptions,
        object? connectionServiceKey = null)
    {
        services.AddNatsDistributedCache(configureOptions, connectionServiceKey);
        // todo: get the INatsConnection out of the service provider, using connectionServiceKey if it is not null
        // to get the keyed service
        // then add the serializer registry from the connection below
        services.AddHybridCache()
            .AddNatsHybridCacheSerializerFactory();
    }

    public static IHybridCacheBuilder AddNatsHybridCacheSerializerFactory(
        this IHybridCacheBuilder builder,
        INatsSerializerRegistry serializerRegistry) =>
        builder.AddSerializerFactory(serializerRegistry.ToHybridCacheSerializerFactory());
}
