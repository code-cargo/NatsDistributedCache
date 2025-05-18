using CodeCargo.NatsDistributedCache.TestUtils.Assertions;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.UnitTests.Cache;

public class TimeExpirationAsyncUnitTests : TestBase
{
    [Fact]
    public async Task AbsoluteExpirationInThePastThrowsAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        await ExceptionAssert.ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await Cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Fact]
    public async Task NegativeRelativeExpirationThrowsAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await ExceptionAssert.ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await Cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public async Task ZeroRelativeExpirationThrowsAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await ExceptionAssert.ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await Cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Fact]
    public async Task NegativeSlidingExpirationThrowsAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await ExceptionAssert.ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await Cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public async Task ZeroSlidingExpirationThrowsAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await ExceptionAssert.ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await Cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.Zero);
    }
}
