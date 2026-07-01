using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
        TimeProvider = new FakeTimeProvider();
        Cache = new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "cache" }),
            mockNatsConnection.Object)
        {
            TimeProvider = TimeProvider,
        };
    }

    /// <summary>
    /// Gets the fake time provider driving the cache's clock
    /// </summary>
    protected FakeTimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the cache
    /// </summary>
    protected NatsCache Cache { get; }

    /// <summary>
    /// Gets the key for the current test method
    /// </summary>
    protected string MethodKey([CallerMemberName] string caller = "") => caller;
}
