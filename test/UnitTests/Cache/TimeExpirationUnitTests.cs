using CodeCargo.NatsDistributedCache.TestUtils.Assertions;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.UnitTests.Cache;

public class TimeExpirationUnitTests : TestBase
{
    [Fact]
    public void AbsoluteExpirationInThePastThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Fact]
    public void NegativeRelativeExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(
                    key,
                    value,
                    new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public void ZeroRelativeExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Fact]
    public void NegativeSlidingExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(
                    key,
                    value,
                    new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.FromMinutes(-1));
    }

    [Fact]
    public void ZeroSlidingExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.Zero);
    }
}
