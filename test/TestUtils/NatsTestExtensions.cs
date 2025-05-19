using CodeCargo.Nats.HybridCacheExtensions;
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
        // Add the NATS hybrid cache with default options
        services.AddNatsHybridCache(options =>
        {
            options.BucketName = "cache";
        });

        return services;
    }
}
