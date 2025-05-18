using CodeCargo.NatsDistributedCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.KeyValueStore;
using NATS.Net;

// The abbreviated example put into the README.md
// Based on Program.cs
public static class Abbreviated
{
    public static async Task Run(string[] args)
    {
        // Set the NATS URL, this normally comes from configuration
        const string natsUrl = "nats://localhost:4222";

        // Create a host builder for a Console application
        // For a Web Application you can use WebApplication.CreateBuilder(args)
        var builder = Host.CreateDefaultBuilder(args);

        // Add services to the container
        builder.ConfigureServices(services =>
        {
            // Add NATS client
            services.AddNats(configureOpts: options => options with { Url = natsUrl });

            // Add a NATS distributed cache
            services.AddNatsDistributedCache(options =>
            {
                options.BucketName = "cache";
            });

            // (Optional) Add HybridCache
            var hybridCacheServices = services.AddHybridCache();

            // (Optional) Use NATS Serializer for HybridCache
            hybridCacheServices.AddSerializerFactory(
                NatsOpts.Default.SerializerRegistry.ToHybridCacheSerializerFactory());

            // Add other services as needed
        });

        // Build the host
        var host = builder.Build();

        // Ensure that the KV Store is created
        var natsConnection = host.Services.GetRequiredService<INatsConnection>();
        var kvContext = natsConnection.CreateKeyValueStoreContext();
        await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache")
        {
            LimitMarkerTTL = TimeSpan.FromSeconds(1)
        });

        // Start the host
        await host.RunAsync();
    }
}
