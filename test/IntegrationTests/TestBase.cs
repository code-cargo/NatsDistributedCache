using System.Runtime.CompilerServices;
using CodeCargo.NatsDistributedCache.TestUtils;
using CodeCargo.NatsDistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

/// <summary>
/// Base class for NATS integration tests that provides test output logging and fixture access
/// </summary>
[Collection(NatsCollection.Name)]
public abstract class TestBase : IAsyncLifetime
{
    private int _disposed;

    /// <summary>
    /// Constructor that sets up the service provider with test output logging
    /// </summary>
    /// <param name="fixture">The NATS integration fixture</param>
    protected TestBase(NatsIntegrationFixture fixture)
    {
        // Get the test output helper from the current test context
        var testContext = TestContext.Current;
        var output = testContext.TestOutputHelper ??
                     throw new InvalidOperationException(
                         "TestOutputHelper was not available in the current test context");

        // Create a service collection and configure logging
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddXUnitTestOutput(output);
        });

        // Configure the service collection with NATS connection
        fixture.ConfigureServices(services);

        // Add the cache
        services.AddNatsDistributedCache(options => options.BucketName = "cache");
        services.AddHybridCacheTestClient();

        // Build service provider
        ServiceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets the service provider configured with test logging and NATS services
    /// </summary>
    protected ServiceProvider ServiceProvider { get; }

    /// <summary>
    /// Gets the NATS connection from the service provider
    /// </summary>
    protected INatsConnection NatsConnection => ServiceProvider.GetRequiredService<INatsConnection>();

    /// <summary>
    /// Gets the cache from the service provider
    /// </summary>
    protected IDistributedCache DistributedCache => ServiceProvider.GetRequiredService<IDistributedCache>();

    /// <summary>
    /// Gets the cache from the service provider
    /// </summary>
    protected HybridCache HybridCache => ServiceProvider.GetRequiredService<HybridCache>();

    /// <summary>
    /// Purge stream before test run
    /// </summary>
    public virtual async ValueTask InitializeAsync() =>
        await NatsConnection
            .CreateJetStreamContext()
            .PurgeStreamAsync("KV_cache", new StreamPurgeRequest(), TestContext.Current.CancellationToken);

    /// <summary>
    /// Dispose
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposed) != 1)
        {
            return;
        }

        await ServiceProvider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Gets the key for the current test method
    /// </summary>
    protected string MethodKey([CallerMemberName] string caller = "") => caller;
}
