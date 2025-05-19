using CodeCargo.Nats.DistributedCache;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;

namespace CodeCargo.Nats.HybridCacheExtensions;

/// <summary>
/// Extension methods for setting up NATS hybrid cache related services in an <see cref="IServiceCollection" />.
/// </summary>
public static class NatsHybridCacheExtensions
{
    /// <summary>
    /// Adds NATS hybrid caching to the specified <see cref="IServiceCollection"/>.
    /// This registers the NATS distributed cache and configures HybridCache to
    /// use the serializer registry from the configured <see cref="INatsConnection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configureNatsOptions">An action to configure <see cref="NatsCacheOptions"/>.</param>
    /// <param name="configureHybridCacheOptions">An optional action to configure <see cref="HybridCacheOptions"/>.</param>
    /// <param name="connectionServiceKey">If set, resolves a keyed <see cref="INatsConnection"/> instance.</param>
    /// <returns>The configured <see cref="IHybridCacheBuilder"/>.</returns>
    public static IHybridCacheBuilder AddNatsHybridCache(
        this IServiceCollection services,
        Action<NatsCacheOptions> configureNatsOptions,
        Action<HybridCacheOptions>? configureHybridCacheOptions = null,
        object? connectionServiceKey = null)
    {
        services.AddNatsDistributedCache(configureNatsOptions, connectionServiceKey);
        var builder = services.AddHybridCache();
        if (configureHybridCacheOptions != null)
        {
            builder.Services.Configure(configureHybridCacheOptions);
        }

        builder.Services.AddSingleton<IHybridCacheSerializerFactory>(sp =>
        {
            var natsConnection = connectionServiceKey == null
                ? sp.GetRequiredService<INatsConnection>()
                : sp.GetRequiredKeyedService<INatsConnection>(connectionServiceKey);

            return natsConnection.Opts.SerializerRegistry.ToHybridCacheSerializerFactory();
        });

        return builder;
    }

    /// <summary>
    /// Registers an <see cref="IHybridCacheSerializerFactory"/> created from the
    /// provided <see cref="INatsSerializerRegistry"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHybridCacheBuilder"/> instance.</param>
    /// <param name="serializerRegistry">The <see cref="INatsSerializerRegistry"/> providing serializers.</param>
    /// <returns>The <see cref="IHybridCacheBuilder"/> for chaining.</returns>
    public static IHybridCacheBuilder AddNatsHybridCacheSerializerFactory(
        this IHybridCacheBuilder builder,
        INatsSerializerRegistry serializerRegistry) =>
        builder.AddSerializerFactory(serializerRegistry.ToHybridCacheSerializerFactory());
}
