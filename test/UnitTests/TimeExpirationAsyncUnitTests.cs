using Microsoft.Extensions.Caching.Distributed;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.NatsDistributedCache.UnitTests;

public class TimeExpirationAsyncUnitTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection;

    public TimeExpirationAsyncUnitTests()
    {
        _mockNatsConnection = new Mock<INatsConnection>();
    }

    [Fact]
    public async Task AbsoluteExpirationInThePastThrowsAsync()
    {
        var cache = CreateCacheInstance();
        var key = "AbsoluteExpirationInThePastThrowsAsync";
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Fact]
    public async Task NegativeRelativeExpirationThrowsAsync()
    {
        var cache = CreateCacheInstance();
        var key = "NegativeRelativeExpirationThrowsAsync";
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public async Task ZeroRelativeExpirationThrowsAsync()
    {
        var cache = CreateCacheInstance();
        var key = "ZeroRelativeExpirationThrowsAsync";
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Fact]
    public async Task NegativeSlidingExpirationThrowsAsync()
    {
        var cache = CreateCacheInstance();
        var key = "NegativeSlidingExpirationThrowsAsync";
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public async Task ZeroSlidingExpirationThrowsAsync()
    {
        var cache = CreateCacheInstance();
        var key = "ZeroSlidingExpirationThrowsAsync";
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.Zero);
    }

    // async twin to ExceptionAssert.ThrowsArgumentOutOfRange
    private static async Task ThrowsArgumentOutOfRangeAsync(Func<Task> test, string paramName, string message, object actualValue)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(test);
        if (paramName is not null)
        {
            Assert.Equal(paramName, ex.ParamName);
        }

        if (message is not null)
        {
            Assert.StartsWith(message, ex.Message); // can have "\r\nParameter name:" etc
        }

        if (actualValue is not null)
        {
            Assert.Equal(actualValue, ex.ActualValue);
        }
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
}
