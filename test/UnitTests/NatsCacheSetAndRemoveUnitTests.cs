using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;
using Xunit;

namespace CodeCargo.NatsDistributedCache.UnitTests;

public class NatsCacheSetAndRemoveUnitTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection;

    public NatsCacheSetAndRemoveUnitTests()
    {
        _mockNatsConnection = new Mock<INatsConnection>();
    }

    private IDistributedCache CreateCacheInstance()
    {
        return new NatsCache(
            Microsoft.Extensions.Options.Options.Create(new NatsCacheOptions
            {
                BucketName = "cache"
            }),
            _mockNatsConnection.Object);
    }

    [Fact]
    public void SetNullValueThrows()
    {
        var cache = CreateCacheInstance();
        byte[] value = null;
        var key = "myKey";

        Assert.Throws<ArgumentNullException>(() => cache.Set(key, value));
    }
}
