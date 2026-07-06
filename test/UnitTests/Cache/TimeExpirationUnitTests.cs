using CodeCargo.Nats.DistributedCache.TestUtils.Assertions;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Cache;

public class TimeExpirationUnitTests : TestBase
{
    [Fact]
    public void AbsoluteExpirationInThePastThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        var expected = TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(1);
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

    [Fact]
    public void TooLargeSlidingExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        // TimeSpan.MaxValue is far beyond the ceiling the NATS TTL encoding supports (int.MaxValue
        // seconds), so the write path must reject it rather than store an entry that later reads back
        // as an undeserializable miss or emits an overflowed TTL header.
        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.MaxValue));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value is too large.",
            TimeSpan.MaxValue);
    }

    [Fact]
    public void TooFarRelativeExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        // A relative expiration beyond the NATS TTL encoding limit (int.MaxValue seconds, ~68 years)
        // would overflow the (int) cast in ToTtlString, so it must be rejected on write.
        var relative = TimeSpan.FromDays(365 * 100);
        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(relative));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value is too large.",
            relative);
    }

    [Fact]
    public void TooFarAbsoluteExpirationThrows()
    {
        var key = MethodKey();
        var value = new byte[1];

        // Same limit for an absolute instant: more than ~68 years out overflows the TTL header.
        var absolute = TimeProvider.GetUtcNow().AddYears(100);
        ExceptionAssert.ThrowsArgumentOutOfRange(
            () =>
            {
                Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(absolute));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration is too far in the future.",
            absolute);
    }
}
