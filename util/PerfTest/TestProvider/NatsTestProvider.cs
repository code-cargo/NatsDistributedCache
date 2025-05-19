using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public class NatsTestProvider : BaseTestProvider
{
    protected override async Task<(DistributedApplication App, string ConnectionString)> StartDistributedApplicationAsync(CancellationToken ct)
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.NatsAppHost>();
        var app = await appHost.BuildAsync();
        await app.StartAsync(ct);

        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await resourceNotificationService.WaitForResourceHealthyAsync("Nats", ct);

        var connectionString = await app.GetConnectionStringAsync("Nats", cancellationToken: ct);
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Cannot find connection string for NATS");
        }

        Console.WriteLine("Aspire started");
        return (app, connectionString);
    }

    protected override void RegisterServices(IServiceCollection services, string connectionString)
    {
        services.AddNatsTestClient(connectionString);
        services.AddNatsDistributedCache(options => options.BucketName = "cache");
    }

    protected override async Task AfterHostBuildAsync(IHost host, string connectionString, CancellationToken ct)
    {
        Console.WriteLine("Creating KV store...");
        var nats = host.Services.GetRequiredService<INatsConnection>();
        var kv = nats.CreateKeyValueStoreContext();
        await kv.CreateOrUpdateStoreAsync(
            new NatsKVConfig("cache")
            {
                LimitMarkerTTL = TimeSpan.FromSeconds(1),
                Storage = NatsKVStorageType.Memory
            },
            ct);
        await nats
            .CreateJetStreamContext()
            .PurgeStreamAsync("KV_cache", new StreamPurgeRequest(), ct);
        Console.WriteLine("KV store created");
    }
}
