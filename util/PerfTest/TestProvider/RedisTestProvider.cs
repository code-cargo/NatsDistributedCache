using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public class RedisTestProvider : BaseTestProvider
{
    protected override async Task<(DistributedApplication App, string ConnectionString)> StartDistributedApplicationAsync(CancellationToken ct)
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RedisAppHost>();
        var app = await appHost.BuildAsync();
        await app.StartAsync(ct);

        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await resourceNotificationService.WaitForResourceHealthyAsync("Redis", ct);

        var connectionString = await app.GetConnectionStringAsync("Redis", cancellationToken: ct);
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Cannot find connection string for Redis");
        }

        Console.WriteLine("Aspire started");
        return (app, connectionString);
    }

    protected override void RegisterServices(IServiceCollection services, string connectionString)
    {
        services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);
    }

    protected override Task AfterHostBuildAsync(IHost host, string connectionString, CancellationToken ct) => Task.CompletedTask;
}
