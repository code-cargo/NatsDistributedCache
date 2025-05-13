using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.Hosting;

namespace CodeCargo.Nats.DistributedCache.TestUtils;

public static class NatsTestExtensions
{
    public static IServiceCollection AddNatsTestClient(this IServiceCollection services, string natsConnectionString) =>
        services.AddNats(configureOpts: options =>
            options with
            {
                Url = natsConnectionString,
                RequestReplyMode = NatsRequestReplyMode.Direct,
            });

    public static IServiceCollection AddHybridCacheTestClient(this IServiceCollection services)
    {
        // Add HybridCache
        var hybridCacheServices = services.AddHybridCache();

        // Use NATS Serializer for HybridCache
        var natsOpts = NatsOpts.Default;
        hybridCacheServices.AddSerializerFactory(
          natsOpts.SerializerRegistry.ToHybridCacheSerializerFactory());
        return services;
    }
}
