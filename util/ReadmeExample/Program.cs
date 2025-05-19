using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CodeCargo.NatsDistributedCache;
using CodeCargo.ReadmeExample;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.KeyValueStore;
using NATS.Net;

// Timeouts
var aspireStartupTimeout = TimeSpan.FromSeconds(30);
var appStartupTimeout = TimeSpan.FromSeconds(30);
var appShutdownTimeout = TimeSpan.FromSeconds(10);

// Start the NatsAppHost application
Console.WriteLine("Starting Aspire...");
var aspireAppHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.NatsAppHost>();
var aspireApp = await aspireAppHost.BuildAsync();
await aspireApp.StartAsync();

// Wait for the NATS resource to be healthy before proceeding
var resourceNotificationService = aspireApp.Services.GetRequiredService<ResourceNotificationService>();
using var startupCts = new CancellationTokenSource(aspireStartupTimeout);
await resourceNotificationService.WaitForResourceHealthyAsync("Nats", startupCts.Token);
Console.WriteLine("Aspire started");

// Get NATS connection string from Aspire
var natsConnectionString = await aspireApp.GetConnectionStringAsync("Nats", cancellationToken: startupCts.Token);
if (string.IsNullOrEmpty(natsConnectionString))
{
    throw new InvalidOperationException("Cannot find connection string for NATS");
}

// Create a host builder for a console application
var builder = Host.CreateDefaultBuilder(args);

// Add services to the container
builder.ConfigureServices(services =>
{
    // Add NATS client
    services.AddNats(configureOpts: options => options with { Url = natsConnectionString });

    // Add a NATS distributed cache
    services.AddNatsDistributedCache(options =>
    {
        options.BucketName = "cache";
    });

    // Add HybridCache
    var hybridCacheServices = services.AddHybridCache();

    // Use NATS Serializer for HybridCache
    hybridCacheServices.AddSerializerFactory(
        NatsOpts.Default.SerializerRegistry.ToHybridCacheSerializerFactory());
        
    // Register our cache services
    services.AddScoped<DistributedCacheService>();
    services.AddScoped<HybridCacheService>();
});

// Build the host
var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

// Create KV store
Console.WriteLine("Creating KV store...");
var natsConnection = host.Services.GetRequiredService<INatsConnection>();
var kvContext = natsConnection.CreateKeyValueStoreContext();
await kvContext.CreateOrUpdateStoreAsync(
    new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) }, startupCts.Token);
Console.WriteLine("KV store created");

// Start the host
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
        // Ignore expected cancellation
    }
});

try
{
    // Wait for the host to start
    await WaitForApplicationStartAsync(lifetime, appStartupTimeout);
    Console.WriteLine("App started");

    // Run the examples using the injected services
    using var scope = host.Services.CreateScope();
    var distributedCacheService = scope.ServiceProvider.GetRequiredService<DistributedCacheService>();
    var hybridCacheService = scope.ServiceProvider.GetRequiredService<HybridCacheService>();
    
    await distributedCacheService.Run();
    await hybridCacheService.Run();

    // Shut down gracefully
    await appCts.CancelAsync();
    await appTask;
}
finally
{
    // Clean up resources
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

return;

static async Task WaitForApplicationStartAsync(IHostApplicationLifetime lifetime, TimeSpan timeout)
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
        // Application started successfully
    }
}

// Methods have been moved to their respective service classes
