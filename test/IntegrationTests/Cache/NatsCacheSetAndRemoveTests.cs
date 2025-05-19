using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class NatsCacheSetAndRemoveTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public void GetMissingKeyReturnsNull()
    {
        var key = MethodKey();
        var result = DistributedCache.Get(key);
        Assert.Null(result);
    }

    [Fact]
    public void SetAndGetReturnsObject()
    {
        var key = MethodKey();
        var value = new byte[1];

        DistributedCache.Set(key, value);

        var result = DistributedCache.Get(key);
        Assert.Equal(value, result);
    }

    [Fact]
    public void SetAndGetWorksWithCaseSensitiveKeys()
    {
        var key1 = MethodKey().ToUpper();
        var key2 = key1.ToLower();
        var value1 = new byte[] { 1 };
        var value2 = new byte[] { 2 };

        DistributedCache.Set(key1, value1);
        DistributedCache.Set(key2, value2);

        var result1 = DistributedCache.Get(key1);
        var result2 = DistributedCache.Get(key2);

        Assert.Equal(value1, result1);
        Assert.Equal(value2, result2);
    }

    [Fact]
    public void SetAlwaysOverwrites()
    {
        var key = MethodKey();
        var value1 = new byte[] { 1 };
        var value2 = new byte[] { 2 };

        DistributedCache.Set(key, value1);
        DistributedCache.Set(key, value2);

        var result = DistributedCache.Get(key);
        Assert.Equal(value2, result);
    }

    [Fact]
    public void RemoveRemoves()
    {
        var key = MethodKey();
        var value = new byte[1];

        DistributedCache.Set(key, value);
        DistributedCache.Remove(key);

        var result = DistributedCache.Get(key);
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
        DistributedCache.Remove(key); // known state
        Assert.Null(DistributedCache.Get(key)); // expect null
        DistributedCache.SetString(key, payload);

        // check raw bytes
        var raw = DistributedCache.Get(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = DistributedCache.GetString(key);
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
        await DistributedCache.RemoveAsync(key); // known state
        Assert.Null(await DistributedCache.GetAsync(key)); // expect null
        await DistributedCache.SetStringAsync(key, payload);

        // check raw bytes
        var raw = await DistributedCache.GetAsync(key);
        Assert.NotNull(raw);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = await DistributedCache.GetStringAsync(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    private static string Hex(byte[] value) => BitConverter.ToString(value);

    private static string Hex(string value) => Hex(Encoding.UTF8.GetBytes(value));
}
