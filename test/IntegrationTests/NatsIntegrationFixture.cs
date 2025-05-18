using Aspire.Hosting;
using CodeCargo.NatsDistributedCache.TestUtils;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

/// <summary>
/// Test fixture that starts an Aspire-hosted NATS server for integration tests
/// </summary>
public class NatsIntegrationFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Type, ServiceLifetime> _registeredServiceTypes = new();
    private DistributedApplication? _app;
    private int _disposed;
    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// Gets the NATS connection
    /// </summary>
    public INatsConnection NatsConnection => _serviceProvider?.GetRequiredService<INatsConnection>()
                                             ?? throw new InvalidOperationException("InitializeAsync was not called");

    /// <summary>
    /// Initializes the fixture by starting the NATS server and creating a connection
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        // Start the NatsAppHost application
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.NatsAppHost>();
        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        // Wait for the NATS resource to be healthy before proceeding
        var resourceNotificationService = _app.Services.GetRequiredService<ResourceNotificationService>();
        using var cts = new CancellationTokenSource(StartupTimeout);
        await resourceNotificationService.WaitForResourceHealthyAsync("Nats", cts.Token);

        // Get NATS connection string from Aspire
        var natsConnectionString = await _app.GetConnectionStringAsync("Nats", cancellationToken: cts.Token);
        if (string.IsNullOrEmpty(natsConnectionString))
        {
            throw new InvalidOperationException("Cannot find connection string for NATS");
        }

        // service provider
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
        });
        services.AddNatsTestClient(natsConnectionString);

        // Track registered singleton services before building the service provider
        foreach (var descriptor in services)
        {
            _registeredServiceTypes[descriptor.ServiceType] = descriptor.Lifetime;
        }

        _serviceProvider = services.BuildServiceProvider();

        // create the KV store
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        await kvContext.CreateOrUpdateStoreAsync(
            new NatsKVConfig("cache")
            {
                LimitMarkerTTL = TimeSpan.FromSeconds(1),
                Storage = NatsKVStorageType.Memory
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Configures the services with the NATS connection
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    public void ConfigureServices(IServiceCollection services)
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException("InitializeAsync must be called before ConfigureServices");
        }

        // Register all singleton services from our service provider
        // Filter out open generic types only
        foreach (var serviceType in _registeredServiceTypes
                     .Where(t => t.Value == ServiceLifetime.Singleton)
                     .Select(t => t.Key)
                     .Where(type => !type.IsGenericTypeDefinition))
        {
            // Get the service instance from our provider and register it with the provided services
            var instance = _serviceProvider.GetService(serviceType);
            if (instance != null)
            {
                services.AddSingleton(serviceType, instance);
            }
        }
    }

    /// <summary>
    /// Disposes the fixture by shutting down the NATS server
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposed) != 1)
        {
            return;
        }

        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_app != null)
        {
            var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(stopCts.Token);
            await _app.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
