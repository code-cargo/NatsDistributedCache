using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using NATS.Client.KeyValueStore;
using Xunit;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

[Collection(NatsCollection.Name)]
public class TimeExpirationAsyncTests : TestBase
{
    public TimeExpirationAsyncTests(NatsIntegrationFixture fixture)
        : base(fixture)
    {
    }

    private IDistributedCache CreateCacheInstance()
    {
        return new NatsCache(
            Microsoft.Extensions.Options.Options.Create(new NatsCacheOptions
            {
                BucketName = "cache"
            }),
            NatsConnection);
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

    // AbsoluteExpirationInThePastThrowsAsync test moved to UnitTests/TimeExpirationAsyncUnitTests.cs
    [Fact]
    public async Task AbsoluteExpirationExpiresAsync()
    {
        var cache = CreateCacheInstance();
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await cache.GetAsync(key);
        }

        Assert.Null(result);
    }

    // TODO: fails with NatsJSApiException: invalid per-message TTL - may not be needed at all?
    // [Fact]
    // public async Task AbsoluteSubSecondExpirationExpiresImmediatelyAsync()
    // {
    //     var cache = CreateCacheInstance();
    //     var key = await GetNameAndReset(cache);
    //     var value = new byte[1];

    //     await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(0.25)));

    //     var result = await cache.GetAsync(key);
    //     Assert.Null(result);
    // }

    // NegativeRelativeExpirationThrowsAsync test moved to UnitTests/TimeExpirationAsyncUnitTests.cs

    // ZeroRelativeExpirationThrowsAsync test moved to UnitTests/TimeExpirationAsyncUnitTests.cs
    [Fact]
    public async Task RelativeExpirationExpiresAsync()
    {
        var cache = CreateCacheInstance();
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4 && result != null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await cache.GetAsync(key);
        }

        Assert.Null(result);
    }

    // NegativeSlidingExpirationThrowsAsync test moved to UnitTests/TimeExpirationAsyncUnitTests.cs

    // ZeroSlidingExpirationThrowsAsync test moved to UnitTests/TimeExpirationAsyncUnitTests.cs
    [Fact]
    public async Task SlidingExpirationExpiresIfNotAccessedAsync()
    {
        var cache = CreateCacheInstance();
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        await Task.Delay(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<NatsKVKeyNotFoundException>(async () => await cache.GetAsync(key));
    }

    [Fact]
    public async Task SlidingExpirationRenewedByAccessAsync()
    {
        var cache = CreateCacheInstance();
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await cache.GetAsync(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<NatsKVKeyNotFoundException>(async () => await cache.GetAsync(key));
    }

    [Fact]
    public async Task SlidingExpirationRenewedByAccessUntilAbsoluteExpirationAsync()
    {
        var cache = CreateCacheInstance();
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(1.1))
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(4)));

        var setTime = DateTime.Now;
        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await cache.GetAsync(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        while ((DateTime.Now - setTime).TotalSeconds < 4)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        await Assert.ThrowsAsync<NatsKVKeyNotFoundException>(async () => await cache.GetAsync(key));
    }

    private static async Task<string> GetNameAndReset(IDistributedCache cache, [CallerMemberName] string caller = "")
    {
        await cache.RemoveAsync(caller);
        return caller;
    }
}
