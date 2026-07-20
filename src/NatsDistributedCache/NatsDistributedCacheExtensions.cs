using System.Diagnostics.Metrics;
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
        services.AddOptions<NatsCacheOptions>()
            .Configure(configureOptions)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BucketName), NatsCacheOptions.BucketNameRequiredMessage)
            .ValidateOnStart();
        services.AddSingleton<IDistributedCache>(sp =>
        {
            var optionsAccessor = sp.GetRequiredService<IOptions<NatsCacheOptions>>();
            var natsConnection = connectionServiceKey == null
                ? sp.GetRequiredService<INatsConnection>()
                : sp.GetRequiredKeyedService<INatsConnection>(connectionServiceKey);
            var logger = sp.GetService<ILogger<NatsCache>>();
            var keyEncoder = sp.GetService<INatsCacheKeyEncoder>();
            var timeProvider = sp.GetService<TimeProvider>();

            // GetService, not GetRequiredService: IMeterFactory is registered by AddMetrics(), which the
            // Generic Host and AddOpenTelemetry().WithMetrics(...) call for you but a bare ServiceCollection
            // does not. Requiring it would break every non-Host consumer; NatsCache falls back to a
            // process-wide static Meter of the same name when this is null.
            var meterFactory = sp.GetService<IMeterFactory>();

            return new NatsCache(optionsAccessor, natsConnection, logger: logger, keyEncoder: keyEncoder)
            {
                TimeProvider = timeProvider ?? TimeProvider.System,
                MeterFactory = meterFactory,
            };
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
