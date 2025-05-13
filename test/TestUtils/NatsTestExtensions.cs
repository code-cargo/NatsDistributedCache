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
        // (Optional) Add HybridCache
        var hybridCacheServices = services.AddHybridCache();

        // (Optional) Use NATS Serializer for HybridCache
        // hybridCacheServices.AddSerializerFactory(
        //   options.SerializerRegistry.ToHybridCacheSerializerFactory());
        return services;
    }
}
