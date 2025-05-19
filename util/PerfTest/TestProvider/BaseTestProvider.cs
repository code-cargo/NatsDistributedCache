using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

public abstract class BaseTestProvider
{
    private static readonly TimeSpan AspireStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AppStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AppShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PerfTestTimeout = TimeSpan.FromMinutes(1);

    protected abstract string BackendName { get; }

    public async Task Run(string[] args)
    {
        Console.WriteLine("Starting Aspire...");
        using var startupCts = new CancellationTokenSource(AspireStartupTimeout);
        var (app, connectionString) = await StartAspire(startupCts.Token);
        Console.WriteLine("Aspire started");

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

            await AfterHostBuild(host, startupCts.Token);

            Console.WriteLine("Starting app...");
            using var appCts = new CancellationTokenSource();
            var ct = appCts.Token;
            var appTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await host.RunAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                    }
                },
                ct);

            try
            {
                using (var cts = new CancellationTokenSource(AppStartupTimeout))
                {
                    using var linked =
                        CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime.ApplicationStarted);
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
                    using var linked =
                        CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime.ApplicationStopping);
                    using var scope = host.Services.CreateScope();
                    var perfTest = scope.ServiceProvider.GetRequiredService<PerfTest>();
                    await perfTest.Run(BackendName, linked.Token);
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

    protected abstract Task<(DistributedApplication App, string ConnectionString)> StartAspire(CancellationToken ct);

    protected abstract void RegisterServices(IServiceCollection services, string connectionString);

    protected virtual Task AfterHostBuild(IHost host, CancellationToken ct) => Task.CompletedTask;
}
