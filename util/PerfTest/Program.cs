using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CodeCargo.Nats.DistributedCache;
using CodeCargo.Nats.DistributedCache.PerfTest;
using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

// Timeouts
var aspireStartupTimeout = TimeSpan.FromSeconds(30);
var appStartupTimeout = TimeSpan.FromSeconds(30);
var appShutdownTimeout = TimeSpan.FromSeconds(10);
var perfTestTimeout = TimeSpan.FromMinutes(1);

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

// Add services directly to the builder
builder.ConfigureServices(services =>
{
    services.AddNatsTestClient(natsConnectionString);
    services.AddNatsDistributedCache(options => options.BucketName = "cache");
    services.AddScoped<PerfTest>();
});

// Build the host
var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

// Create KV store
Console.WriteLine("Creating KV store...");
var nats = host.Services.GetRequiredService<INatsConnection>();
var kv = nats.CreateKeyValueStoreContext();
await kv.CreateOrUpdateStoreAsync(
    new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) },
    startupCts.Token);
Console.WriteLine("KV store created");

// Run the host
Console.WriteLine("Starting app...");
var appCts = new CancellationTokenSource();
var appTask = Task.Run(async () =>
{
    try
    {
        await host.RunAsync(appCts.Token);
    }
    catch (OperationCanceledException) when (appCts.IsCancellationRequested)
    {
        // ignore
    }
});

try
{
    try
    {
        // startup
        using (var cts = new CancellationTokenSource(appStartupTimeout))
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cts.Token,
                lifetime.ApplicationStarted);
            try
            {
                await Task.Delay(appStartupTimeout, linkedCts.Token);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStarted.IsCancellationRequested)
            {
                Console.WriteLine("App Started");
            }
        }

        using (var cts = new CancellationTokenSource(perfTestTimeout))
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cts.Token,
                lifetime.ApplicationStopping);
            using var scope = host.Services.CreateScope();
            var perfTest = scope.ServiceProvider.GetRequiredService<PerfTest>();
            await perfTest.Run(linkedCts.Token);
        }
    }
    catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
    {
        // ignore
    }

    await appCts.CancelAsync();
    await appTask;
}
finally
{
    // Clean up resources
    using var stopCts = new CancellationTokenSource(appShutdownTimeout);
    try
    {
        await aspireApp.StopAsync(stopCts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error stopping application: {ex.Message}");
    }

    await aspireApp.DisposeAsync();
}
