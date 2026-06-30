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
    public void GetTtlComputesRelativeExpirationFromProviderTime()
    {
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
}
