using System.Threading;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public class RedisTestProvider : BaseTestProvider
{
    protected override string BackendName => "Redis";

    protected override async Task<(DistributedApplication App, string ConnectionString)> StartAspire(
        CancellationToken ct)
    {
        try
        {
            // Create and start the Redis app host
            var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RedisAppHost>();
            var app = await appHost.BuildAsync();
            await app.StartAsync(ct);

            Console.WriteLine("Waiting for Redis to become available...");

            // Get the connection string from Aspire
            // Wait for the Redis resource to become available
            try
            {
                var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
                await resourceNotificationService.WaitForResourceHealthyAsync("Redis", ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to wait for Redis resource: {ex.Message}");

                // Continue anyway and try to get the connection string
            }

            // Get the connection string from Aspire
            string? connectionString;
            try
            {
                // Try to get the connection string from Aspire
                connectionString = await app.GetConnectionStringAsync("Redis", cancellationToken: ct);
                if (!string.IsNullOrEmpty(connectionString))
                {
                    Console.WriteLine($"Using connection string from Aspire: {connectionString}");
                }
                else
                {
                    throw new InvalidOperationException("Aspire returned empty Redis connection string");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get Redis connection: {ex.Message}");
                throw new InvalidOperationException(
                    "Could not establish Redis connection. Make sure the Redis container is running.", ex);
            }

            Console.WriteLine($"Connecting to Redis at: {connectionString}");
            return (app, connectionString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting Redis: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw;
        }
    }

    protected override void RegisterServices(IServiceCollection services, string connectionString) =>
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = connectionString;
        });

    protected override Task AfterHostBuild(IHost host, CancellationToken ct) => Task.CompletedTask;
}
