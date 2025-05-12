using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.UnitTests;

public abstract class TestBase
{
    protected TestBase()
    {
        var mockNatsConnection = new Mock<INatsConnection>();
        var opts = new NatsOpts { LoggerFactory = new LoggerFactory() };
        mockNatsConnection.SetupGet(m => m.Opts).Returns(opts);
        var connection = new NatsConnection(opts);
        mockNatsConnection.SetupGet(m => m.Connection).Returns(connection);
        Cache = new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "cache" }),
            mockNatsConnection.Object);
    }

    /// <summary>
    /// Gets the cache
    /// </summary>
    protected IDistributedCache Cache { get; }

    /// <summary>
    /// Gets the key for the current test method
    /// </summary>
    protected string MethodKey([CallerMemberName] string caller = "") => caller;
}
