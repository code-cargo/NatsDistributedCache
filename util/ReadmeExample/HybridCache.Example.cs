using CodeCargo.Nats.HybridCacheExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Example used in the README.md "Use with `HybridCache`" section
public static class HybridCacheExample
{
    public static async Task Run(string[] args)
    {
        // Set the NATS URL, this normally comes from configuration
        const string natsUrl = "nats://localhost:4222";

        // Create a host builder for a Console application
        // For a Web Application you can use WebApplication.CreateBuilder(args)
        var builder = Host.CreateDefaultBuilder(args);
        builder.ConfigureServices(services =>
        {
            services.AddNatsClient(natsBuilder =>
                natsBuilder.ConfigureOptions(optsBuilder => optsBuilder.Configure(opts =>
                    opts.Opts = opts.Opts with { Url = natsUrl })));
            services.AddNatsHybridCache(options =>
            {
                options.BucketName = "cache";
            });
        });

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
