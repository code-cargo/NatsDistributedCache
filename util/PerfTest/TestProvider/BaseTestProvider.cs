using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public abstract class BaseTestProvider
{
    protected virtual TimeSpan AspireStartupTimeout => TimeSpan.FromSeconds(30);
    protected virtual TimeSpan AppStartupTimeout => TimeSpan.FromSeconds(30);
    protected virtual TimeSpan AppShutdownTimeout => TimeSpan.FromSeconds(10);
    protected virtual TimeSpan PerfTestTimeout => TimeSpan.FromMinutes(1);

    protected abstract Task<(DistributedApplication App, string ConnectionString)> StartDistributedApplicationAsync(CancellationToken ct);
    protected abstract void RegisterServices(IServiceCollection services, string connectionString);
    protected virtual Task AfterHostBuildAsync(IHost host, string connectionString, CancellationToken ct) => Task.CompletedTask;

    public async Task RunAsync(string[] args)
    {
        Console.WriteLine("Starting Aspire...");
        using var startupCts = new CancellationTokenSource(AspireStartupTimeout);
        var (app, connectionString) = await StartDistributedApplicationAsync(startupCts.Token);
        try
        {
            var builder = Host.CreateDefaultBuilder(args);
            builder.ConfigureServices(services =>
            {
                RegisterServices(services, connectionString);
                services.AddScoped<PerfTest>();
            });

            var host = builder.Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            await AfterHostBuildAsync(host, connectionString, startupCts.Token);

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
                using (var cts = new CancellationTokenSource(AppStartupTimeout))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime.ApplicationStarted);
                    try
                    {
                        await Task.Delay(AppStartupTimeout, linked.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.ApplicationStarted.IsCancellationRequested)
                    {
                        Console.WriteLine("App Started");
                    }
                }

                using (var cts = new CancellationTokenSource(PerfTestTimeout))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime.ApplicationStopping);
                    using var scope = host.Services.CreateScope();
                    var perfTest = scope.ServiceProvider.GetRequiredService<PerfTest>();
                    await perfTest.Run(linked.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
            }

            await appCts.CancelAsync();
            await appTask;
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(AppShutdownTimeout);
            try
            {
                await app.StopAsync(stopCts.Token);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Error stopping application: {ex.Message}");
            }

            await app.DisposeAsync();
        }
    }
}
