using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Cache;

/// <summary>
/// Verifies that expiration logic reads the clock from the injected <see cref="System.TimeProvider"/>,
/// enabling deterministic, instant time-expiration tests without real delays.
/// </summary>
public class TimeProviderExpirationUnitTests : TestBase
{
    [Fact]
    public void AbsoluteExpirationExpiresWhenTimeAdvances()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(30)));

        Assert.False(Cache.IsAbsolutelyExpired(entry));

        TimeProvider.Advance(TimeSpan.FromSeconds(31));

        Assert.True(Cache.IsAbsolutelyExpired(entry));
    }

    [Fact]
    public void AbsoluteExpirationIsExpiredAtExactInstant()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(30)));

        // Advance to exactly the absolute expiration instant: the boundary is inclusive.
        TimeProvider.Advance(TimeSpan.FromSeconds(30));

        Assert.True(Cache.IsAbsolutelyExpired(entry));
    }

    [Fact]
    public void SlidingOnlyEntryIsNeverAbsolutelyExpired()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(30)));

        TimeProvider.Advance(TimeSpan.FromHours(1));

        // Absolute expiration only applies to entries with an absolute expiration set.
        Assert.False(Cache.IsAbsolutelyExpired(entry));
    }

    [Fact]
    public void CreateCacheEntryComputesAbsoluteExpirationFromProviderTime()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));

        Assert.Equal(TimeProvider.GetUtcNow().AddMinutes(5), entry.AbsoluteExpiration);
        Assert.Null(entry.SlidingExpirationTicks);
    }

    [Fact]
    public void GetTtlReturnsConfiguredRelativeExpiration()
    {
        // A relative expiration yields a TTL equal to the configured duration, independent of the clock.
        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));

        Assert.Equal(TimeSpan.FromMinutes(5), ttl);
    }

    [Fact]
    public void GetTtlComputesAbsoluteExpirationFromProviderTime()
    {
        var absolute = TimeProvider.GetUtcNow().AddMinutes(10);

        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions().SetAbsoluteExpiration(absolute));

        Assert.Equal(TimeSpan.FromMinutes(10), ttl);
    }

    [Fact]
    public void GetTtlReturnsSlidingExpirationWhenNoAbsolute()
    {
        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(3)));

        Assert.Equal(TimeSpan.FromMinutes(3), ttl);
    }

    [Fact]
    public void GetTtlReturnsMinimumOfSlidingAndAbsolute()
    {
        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(2))
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(10)));

        Assert.Equal(TimeSpan.FromMinutes(2), ttl);
    }

    [Fact]
    public void GetTtlAcceptsMaxSlidingExpiration()
    {
        // The ceiling itself is valid: it round-trips through both the serializer and the
        // second-granularity NATS TTL encoding without overflowing.
        var max = TimeSpan.FromTicks(CacheEntryBinarySerializer.MaxTtlTicks);

        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions().SetSlidingExpiration(max));

        Assert.Equal(max, ttl);
    }

    [Fact]
    public void GetTtlWithFarAbsoluteAndSlidingReturnsSliding()
    {
        // A far-future absolute paired with a small sliding must not be rejected: the effective TTL is
        // the (encodable) sliding window, not the huge absolute one, so the ceiling check is not tripped.
        var ttl = Cache.GetTtl(new DistributedCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromDays(365 * 100))
            .SetSlidingExpiration(TimeSpan.FromMinutes(10)));

        Assert.Equal(TimeSpan.FromMinutes(10), ttl);
    }

    // DateTimeOffset.MaxValue / TimeSpan.MaxValue mean "cache forever": no TTL, not an out-of-range throw.
    [Fact]
    public void GetTtlTreatsMaxAbsoluteInstantAsNoExpiration() =>
        Assert.Null(Cache.GetTtl(new DistributedCacheEntryOptions().SetAbsoluteExpiration(DateTimeOffset.MaxValue)));

    [Fact]
    public void GetTtlTreatsMaxRelativeExpirationAsNoExpiration() =>
        Assert.Null(Cache.GetTtl(new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.MaxValue)));

    [Fact]
    public void GetTtlTreatsMaxSlidingExpirationAsNoExpiration() =>
        Assert.Null(Cache.GetTtl(new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.MaxValue)));

    [Fact]
    public void CreateCacheEntryTreatsMaxValueSlidingAsNoExpiration()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.MaxValue));

        Assert.Null(entry.SlidingExpirationTicks);
        Assert.Null(entry.AbsoluteExpiration);
    }

    [Fact]
    public void CreateCacheEntryTreatsMaxValueAbsoluteAsNoExpiration()
    {
        var entry = Cache.CreateCacheEntry(
            new byte[1],
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(DateTimeOffset.MaxValue));

        Assert.Null(entry.AbsoluteExpiration);
        Assert.Null(entry.SlidingExpirationTicks);
    }
}
