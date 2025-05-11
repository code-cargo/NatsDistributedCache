using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.IntegrationTests.Cache;

public class TimeExpirationTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public void AbsoluteExpirationExpires()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = Cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = Cache.Get(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public void RelativeExpirationExpires()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = Cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = Cache.Get(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationExpiresIfNotAccessed()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = Cache.Get(key);
        Assert.Equal(value, result);

        Thread.Sleep(TimeSpan.FromSeconds(3));

        result = Cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationRenewedByAccess()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = Cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));

            result = Cache.Get(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));

        result = Cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationRenewedByAccessUntilAbsoluteExpiration()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(1.1))
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(4)));

        var setTime = DateTime.Now;
        var result = Cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));

            result = Cache.Get(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        while ((DateTime.Now - setTime).TotalSeconds < 4)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
        }

        result = Cache.Get(key);
        Assert.Null(result);
    }
}
