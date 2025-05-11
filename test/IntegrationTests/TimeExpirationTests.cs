using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

[Collection(NatsCollection.Name)]
public class TimeExpirationTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public void AbsoluteExpirationExpires()
    {
        var cache = CreateCacheInstance();
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = cache.Get(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public void RelativeExpirationExpires()
    {
        var cache = CreateCacheInstance();
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = cache.Get(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationExpiresIfNotAccessed()
    {
        var cache = CreateCacheInstance();
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        Thread.Sleep(TimeSpan.FromSeconds(3));

        result = cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationRenewedByAccess()
    {
        var cache = CreateCacheInstance();
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));

            result = cache.Get(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));

        result = cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SlidingExpirationRenewedByAccessUntilAbsoluteExpiration()
    {
        var cache = CreateCacheInstance();
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(1.1))
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(4)));

        var setTime = DateTime.Now;
        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));

            result = cache.Get(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        while ((DateTime.Now - setTime).TotalSeconds < 4)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
        }

        result = cache.Get(key);
        Assert.Null(result);
    }

    private static string GetNameAndReset(IDistributedCache cache, [CallerMemberName] string caller = "")
    {
        cache.Remove(caller);
        return caller;
    }

    private IDistributedCache CreateCacheInstance() =>
        new NatsCache(
            Microsoft.Extensions.Options.Options.Create(new NatsCacheOptions
            {
                BucketName = "cache"
            }),
            NatsConnection);
}
