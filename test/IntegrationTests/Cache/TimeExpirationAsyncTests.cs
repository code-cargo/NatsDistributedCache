using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class TimeExpirationAsyncTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task AbsoluteExpirationExpiresAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = await DistributedCache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await DistributedCache.GetAsync(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public async Task RelativeExpirationExpiresAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = await DistributedCache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await DistributedCache.GetAsync(key);
        }

        Assert.Null(result);
    }

    [Fact]
    public async Task SlidingExpirationExpiresIfNotAccessedAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await DistributedCache.GetAsync(key);
        Assert.Equal(value, result);

        await Task.Delay(TimeSpan.FromSeconds(3));

        result = await DistributedCache.GetAsync(key);
        Assert.Null(result);
    }

    [Fact]
    public async Task SlidingExpirationRenewedByAccessAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await DistributedCache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await DistributedCache.GetAsync(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        result = await DistributedCache.GetAsync(key);
        Assert.Null(result);
    }

    [Fact]
    public async Task SlidingExpirationRenewedByAccessUntilAbsoluteExpirationAsync()
    {
        var key = MethodKey();
        var value = new byte[1];

        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(1.1))
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(4)));

        var setTime = DateTime.Now;
        var result = await DistributedCache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await DistributedCache.GetAsync(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        while ((DateTime.Now - setTime).TotalSeconds < 4)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        result = await DistributedCache.GetAsync(key);
        Assert.Null(result);
    }
}
