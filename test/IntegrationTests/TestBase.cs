using CodeCargo.NatsDistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

/// <summary>
/// Base class for NATS integration tests that provides test output logging and fixture access
/// </summary>
public abstract class TestBase : IAsyncDisposable
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
        var output = testContext.TestOutputHelper;
        if (output == null)
        {
            throw new InvalidOperationException("TestOutputHelper was not available in the current test context");
        }

        // Create a service collection and configure logging
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddXUnitTestOutput(output);
        });

        // Configure the service collection with NATS connection
        fixture.ConfigureServices(services);

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
    /// Cleanup after the test
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
}
