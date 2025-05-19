using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Extension methods for setting up NATS distributed cache related services in an <see cref="IServiceCollection" />.
/// </summary>
public static class NatsDistributedCacheExtensions
{
    /// <summary>
    /// Adds NATS distributed caching services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configureOptions">An <see cref="Action{NatsCacheOptions}"/> to configure the provided
    /// <see cref="NatsCacheOptions"/>.</param>
    /// <param name="connectionServiceKey">If set, used keyed service to resolve <see cref="INatsConnection"/></param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    public static IServiceCollection AddNatsDistributedCache(
        this IServiceCollection services,
        Action<NatsCacheOptions> configureOptions,
        object? connectionServiceKey = null)
    {
        services.AddOptions();
        services.Configure(configureOptions);
        services.AddSingleton<IDistributedCache>(sp =>
        {
            var optionsAccessor = sp.GetRequiredService<IOptions<NatsCacheOptions>>();
            var logger = sp.GetService<ILogger<NatsCache>>();

            var natsConnection = connectionServiceKey == null
                ? sp.GetRequiredService<INatsConnection>()
                : sp.GetRequiredKeyedService<INatsConnection>(connectionServiceKey);

            return logger != null
                ? new NatsCache(optionsAccessor, logger, natsConnection)
                : new NatsCache(optionsAccessor, natsConnection);
        });

        return services;
    }

    /// <summary>
    /// Creates an <see cref="IHybridCacheSerializerFactory"/> that uses the provided
    /// <see cref="INatsSerializerRegistry"/> to perform serialization.
    /// </summary>
    /// <param name="serializerRegistry">The <see cref="INatsSerializerRegistry"/> instance</param>
    /// <returns>The <see cref="IHybridCacheSerializerFactory"/> instance</returns>
    public static IHybridCacheSerializerFactory ToHybridCacheSerializerFactory(
        this INatsSerializerRegistry serializerRegistry) =>
        new NatsHybridCacheSerializerFactory(serializerRegistry);
}
