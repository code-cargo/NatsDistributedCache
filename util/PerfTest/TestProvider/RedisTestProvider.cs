using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Projects;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public class RedisTestProvider : BaseTestProvider
{
    protected override string BackendName => "Redis";

    protected override async Task<(DistributedApplication App, string ConnectionString)> StartAspire(
        CancellationToken ct)
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<RedisAppHost>(ct);
        var app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);

        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await resourceNotificationService.WaitForResourceHealthyAsync("Redis", ct);

        var connectionString = await app.GetConnectionStringAsync("Redis", cancellationToken: ct);
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Cannot find connection string for Redis");
        }

        return (app, connectionString);
    }

    protected override void RegisterServices(IServiceCollection services, string connectionString) =>
        services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);
}
