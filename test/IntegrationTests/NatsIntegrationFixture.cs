using System;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using NATS.Client.Core;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

/// <summary>
/// Test fixture that starts an Aspire-hosted NATS server for integration tests
/// </summary>
public class NatsIntegrationFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private DistributedApplication? _app;
    private INatsConnection? _natsConnection;

    /// <summary>
    /// Gets the NATS connection for accessing the test NATS server
    /// </summary>
    public INatsConnection NatsConnection => _natsConnection ?? throw new InvalidOperationException(
        "NATS connection not initialized. Make sure InitializeAsync has been called.");

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

        // Create a NATS connection
        var opts = new NatsOpts
        {
            Url = natsConnectionString,
            Name = "IntegrationTest",
            RequestReplyMode = NatsRequestReplyMode.Direct,
        };
        _natsConnection = new NatsConnection(opts);
    }

    /// <summary>
    /// Disposes the fixture by shutting down the NATS server
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_natsConnection != null)
        {
            await _natsConnection.DisposeAsync();
            _natsConnection = null;
        }

        if (_app != null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        GC.SuppressFinalize(this);
    }
}
