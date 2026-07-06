using CodeCargo.Nats.DistributedCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Example used in the README.md "Use `IDistributedCache` Directly" section
public static class DistributedCacheExample
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
            services.AddNatsDistributedCache(options =>
            {
                options.BucketName = "cache";

                // Create the KV bucket on first use if it doesn't already exist.
                // Omit this if you pre-create the bucket yourself (see Requirements).
                options.CreateBucketIfNotExists = true;
            });
        });

        var host = builder.Build();

        // Start the host
        await host.RunAsync();
    }
}
