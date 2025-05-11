using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

[Collection(NatsCollection.Name)]
public class NatsCacheSetAndRemoveTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public void GetMissingKeyReturnsNull()
    {
        var cache = CreateCacheInstance();
        var key = "non-existent-key";

        var result = cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SetAndGetReturnsObject()
    {
        var cache = CreateCacheInstance();
        var value = new byte[1];
        var key = "myKey";

        cache.Set(key, value);

        var result = cache.Get(key);
        Assert.Equal(value, result);
    }

    [Fact]
    public void SetAndGetWorksWithCaseSensitiveKeys()
    {
        var cache = CreateCacheInstance();
        var value1 = new byte[1] { 1 };
        var value2 = new byte[1] { 2 };
        var key1 = "myKey";
        var key2 = "Mykey";

        cache.Set(key1, value1);
        cache.Set(key2, value2);

        var result1 = cache.Get(key1);
        var result2 = cache.Get(key2);

        Assert.Equal(value1, result1);
        Assert.Equal(value2, result2);
    }

    [Fact]
    public void SetAlwaysOverwrites()
    {
        var cache = CreateCacheInstance();
        var value1 = new byte[1] { 1 };
        var value2 = new byte[1] { 2 };
        var key = "myKey";

        cache.Set(key, value1);
        cache.Set(key, value2);

        var result = cache.Get(key);
        Assert.Equal(value2, result);
    }

    [Fact]
    public void RemoveRemoves()
    {
        var cache = CreateCacheInstance();
        var value = new byte[1];
        var key = "myKey";

        cache.Set(key, value);
        cache.Remove(key);

        var result = cache.Get(key);
        Assert.Null(result);
    }

    // SetNullValueThrows test moved to UnitTests/NatsCacheSetAndRemoveUnitTests.cs
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("abc def ghi jkl mno pqr stu vwx yz!")]
    public void SetGetNonNullString(string payload)
    {
        var cache = CreateCacheInstance();
        var key = Me();
        cache.Remove(key); // known state
        Assert.Null(cache.Get(key)); // expect null
        cache.SetString(key, payload);

        // check raw bytes
        var raw = cache.Get(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = cache.GetString(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("abc def ghi jkl mno pqr stu vwx yz!")]
    public async Task SetGetNonNullStringAsync(string payload)
    {
        var cache = CreateCacheInstance();
        var key = Me();
        await cache.RemoveAsync(key); // known state
        Assert.Null(await cache.GetAsync(key)); // expect null
        await cache.SetStringAsync(key, payload);

        // check raw bytes
        var raw = await cache.GetAsync(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = await cache.GetStringAsync(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    private static string Hex(byte[] value) => BitConverter.ToString(value);

    private static string Hex(string value) => Hex(Encoding.UTF8.GetBytes(value));

    private static string Me([CallerMemberName] string caller = "") => caller;

    private IDistributedCache CreateCacheInstance()
    {
        return new NatsCache(
            Microsoft.Extensions.Options.Options.Create(new NatsCacheOptions
            {
                BucketName = "cache"
            }),
            NatsConnection);
    }
}
