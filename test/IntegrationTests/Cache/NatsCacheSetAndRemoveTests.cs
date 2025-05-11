using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.IntegrationTests.Cache;

public class NatsCacheSetAndRemoveTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public void GetMissingKeyReturnsNull()
    {
        var key = MethodKey();
        var result = Cache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SetAndGetReturnsObject()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value);

        var result = Cache.Get(key);
        Assert.Equal(value, result);
    }

    [Fact]
    public void SetAndGetWorksWithCaseSensitiveKeys()
    {
        var key1 = MethodKey().ToUpper();
        var key2 = key1.ToLower();
        var value1 = new byte[] { 1 };
        var value2 = new byte[] { 2 };

        Cache.Set(key1, value1);
        Cache.Set(key2, value2);

        var result1 = Cache.Get(key1);
        var result2 = Cache.Get(key2);

        Assert.Equal(value1, result1);
        Assert.Equal(value2, result2);
    }

    [Fact]
    public void SetAlwaysOverwrites()
    {
        var key = MethodKey();
        var value1 = new byte[] { 1 };
        var value2 = new byte[] { 2 };

        Cache.Set(key, value1);
        Cache.Set(key, value2);

        var result = Cache.Get(key);
        Assert.Equal(value2, result);
    }

    [Fact]
    public void RemoveRemoves()
    {
        var key = MethodKey();
        var value = new byte[1];

        Cache.Set(key, value);
        Cache.Remove(key);

        var result = Cache.Get(key);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("abc def ghi jkl mno pqr stu vwx yz!")]
    public void SetGetNonNullString(string payload)
    {
        var key = MethodKey();
        Cache.Remove(key); // known state
        Assert.Null(Cache.Get(key)); // expect null
        Cache.SetString(key, payload);

        // check raw bytes
        var raw = Cache.Get(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = Cache.GetString(key);
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
        var key = MethodKey();
        await Cache.RemoveAsync(key); // known state
        Assert.Null(await Cache.GetAsync(key)); // expect null
        await Cache.SetStringAsync(key, payload);

        // check raw bytes
        var raw = await Cache.GetAsync(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = await Cache.GetStringAsync(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    private static string Hex(byte[] value) => BitConverter.ToString(value);

    private static string Hex(string value) => Hex(Encoding.UTF8.GetBytes(value));
}
