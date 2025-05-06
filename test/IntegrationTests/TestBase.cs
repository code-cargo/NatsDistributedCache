using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using CodeCargo.NatsDistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Xunit;
using Xunit.Sdk;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

/// <summary>
/// Base class for NATS integration tests that provides test output logging and fixture access
/// </summary>
public abstract class TestBase(NatsIntegrationFixture fixture) : IAsyncLifetime
{
    private const string ServiceProviderKey = "ServiceProvider";
    private readonly NatsIntegrationFixture _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    private static readonly ConcurrentBag<ServiceProvider> _serviceProviders = new();
    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// Gets the NATS connection from the fixture
    /// </summary>
    protected INatsConnection NatsConnection => _fixture.NatsConnection;

    /// <summary>
    /// Gets the service provider configured with test logging and NATS services
    /// </summary>
    protected IServiceProvider ServiceProvider => _serviceProvider ??
        throw new InvalidOperationException("Service provider not initialized. Make sure InitializeAsync has been called.");

    // Static constructor to register for test class configuration
    static TestBase()
    {
        // xUnit v3 will call ConfigureTestClass
    }

    /// <summary>
    /// Configures the test class - this method is called by xUnit v3
    /// </summary>
    /// <param name="context">The test context</param>
    public static void ConfigureTestClass(TestContext context)
    {
        // Get the test output helper
        var output = context.TestOutputHelper;
        if (output == null)
            return;

        // Create a service collection and configure logging
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddXUnitTestOutput(output);
        });

        // Build the service provider and store it in the context's key-value storage
        var serviceProvider = services.BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);
        context.KeyValueStorage[ServiceProviderKey] = serviceProvider;
    }

    /// <summary>
    /// Initializes the test by ensuring the KV store exists
    /// </summary>
    public virtual async ValueTask InitializeAsync()
    {
        // Setup service provider for this instance
        var services = new ServiceCollection();

        // Add NATS connection directly
        services.AddSingleton<INatsConnection>(_fixture.NatsConnection);

        // Add logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
        });

        // Build service provider
        _serviceProvider = services.BuildServiceProvider();
        _serviceProviders.Add(_serviceProvider);

        // Create or ensure KV store exists
        var jsContext = NatsConnection.CreateJetStreamContext();
        var kvContext = new NatsKVContext(jsContext);
        await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache"));
    }

    /// <summary>
    /// Cleanup after the test
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null;
        }
    }
}
