using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CodeCargo.Nats.HybridCacheExtensions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

namespace CodeCargo.ReadmeExample;

public static class HybridCacheStartup
{
    public static async Task RunAsync(string[] args)
    {
        var aspireStartupTimeout = TimeSpan.FromSeconds(30);
        var appStartupTimeout = TimeSpan.FromSeconds(30);
        var appShutdownTimeout = TimeSpan.FromSeconds(10);

        Console.WriteLine("Starting Aspire...");
        var aspireAppHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.NatsAppHost>();
        var aspireApp = await aspireAppHost.BuildAsync();
        await aspireApp.StartAsync();

        var resourceNotificationService = aspireApp.Services.GetRequiredService<ResourceNotificationService>();
        using var startupCts = new CancellationTokenSource(aspireStartupTimeout);
        await resourceNotificationService.WaitForResourceHealthyAsync("Nats", startupCts.Token);
        Console.WriteLine("Aspire started");

        var natsConnectionString = await aspireApp.GetConnectionStringAsync("Nats", cancellationToken: startupCts.Token);
        if (string.IsNullOrEmpty(natsConnectionString))
        {
            throw new InvalidOperationException("Cannot find connection string for NATS");
        }

        var builder = Host.CreateDefaultBuilder(args);
        builder.ConfigureServices(services =>
        {
            services.AddNatsClient(natsBuilder => natsBuilder.ConfigureOptions(opts => opts with { Url = natsConnectionString }));
            services.AddNatsHybridCache(options =>
            {
                options.BucketName = "cache";
            });

            services.AddScoped<HybridCacheService>();
        });

        var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        Console.WriteLine("Creating KV store...");
        var natsConnection = host.Services.GetRequiredService<INatsConnection>();
        var kvContext = natsConnection.CreateKeyValueStoreContext();
        await kvContext.CreateOrUpdateStoreAsync(
            new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) }, startupCts.Token);
        Console.WriteLine("KV store created");

        Console.WriteLine("Starting app...");
        using var appCts = new CancellationTokenSource();
        var appTask = Task.Run(async () =>
        {
            try
            {
                await host.RunAsync(appCts.Token);
            }
            catch (OperationCanceledException) when (appCts.IsCancellationRequested)
            {
            }
        });

        try
        {
            await WaitForApplicationStartAsync(lifetime, appStartupTimeout);
            Console.WriteLine("App started");

            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<HybridCacheService>();
            await service.Run();

            await appCts.CancelAsync();
            await appTask;
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(appShutdownTimeout);
            try
            {
                Console.WriteLine("Stopping app...");
                await aspireApp.StopAsync(stopCts.Token);
                Console.WriteLine("App stopped");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Error stopping app: {ex.Message}");
            }

            await aspireApp.DisposeAsync();
        }
    }

    private static async Task WaitForApplicationStartAsync(IHostApplicationLifetime lifetime, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token,
            lifetime.ApplicationStarted);
        try
        {
            await Task.Delay(timeout, linkedCts.Token);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStarted.IsCancellationRequested)
        {
        }
    }
}

public class HybridCacheService(HybridCache cache, ILogger<HybridCacheService> logger)
{
    public async Task Run()
    {
        logger.LogInformation("------------------------------------------");
        logger.LogInformation("HybridCache example");

        const string key = "hybrid-cache-greeting";

        var result = await cache.GetOrCreateAsync<Person>(
            key,
            _ => ValueTask.FromResult(new Person("John Doe", 30)),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1) });
        logger.LogInformation("Got/created value from cache: {Result}", result);

        await cache.RemoveAsync(key);
        logger.LogInformation("Removed value from cache");
    }

    private record Person(string Name, int Age);
}
