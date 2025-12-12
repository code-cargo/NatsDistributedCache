using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Extensions;

/// <summary>
/// Integration tests for NatsExtensions.PutWithTtlAsync
/// </summary>
public class NatsExtensionsPutWithTtlTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task PutWithTtlAsync_StoresValue()
    {
        var key = MethodKey();
        var value = "test-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var revision = await store.PutWithTtlAsync(key, value);

        Assert.True(revision > 0);

        var entry = await store.GetEntryAsync<string>(key);
        Assert.Equal(value, entry.Value);
    }

    [Fact]
    public async Task PutWithTtlAsync_WithTtl_ExpiresAfterTtl()
    {
        var key = MethodKey();
        var value = "test-value-ttl";
        var ttl = TimeSpan.FromSeconds(2);

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        await store.PutWithTtlAsync(key, value, ttl);

        // Value should exist immediately
        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(value, entry.Value.Value);

        // Wait for TTL to expire
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Value should be expired
        var expiredEntry = await store.TryGetEntryAsync<string>(key);
        Assert.False(expiredEntry.Success);
    }

    [Fact]
    public async Task PutWithTtlAsync_WithoutTtl_DoesNotExpire()
    {
        var key = MethodKey();
        var value = "test-value-no-ttl";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        await store.PutWithTtlAsync(key, value);

        // Value should exist immediately
        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(value, entry.Value.Value);

        // Wait a bit
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Value should still exist
        var stillExists = await store.TryGetEntryAsync<string>(key);
        Assert.True(stillExists.Success);
        Assert.Equal(value, stillExists.Value.Value);
    }

    [Fact]
    public async Task TryPutWithTtlAsync_WithEmptyKey_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync(string.Empty, "value");

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("empty", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryPutWithTtlAsync_WithKeyStartingWithPeriod_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync(".invalid-key", "value");

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("period", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryPutWithTtlAsync_WithKeyEndingWithPeriod_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync("invalid-key.", "value");

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("period", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryPutWithTtlAsync_WithInvalidCharacters_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync("invalid key with spaces", "value");

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("invalid", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryPutWithTtlAsync_WithValidKey_ReturnsSuccess()
    {
        var key = MethodKey();
        var value = "test-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync(key, value);

        Assert.True(result.Success);
        Assert.True(result.Value > 0);
    }

    [Theory]
    [InlineData("simple-key")]
    [InlineData("key_with_underscore")]
    [InlineData("key/with/slashes")]
    [InlineData("key.with.periods")]
    [InlineData("key=with=equals")]
    [InlineData("MixedCase123")]
    public async Task TryPutWithTtlAsync_WithValidKeyFormats_Succeeds(string key)
    {
        var value = "test-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryPutWithTtlAsync(key, value);

        Assert.True(result.Success);

        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(value, entry.Value.Value);
    }

    [Fact]
    public async Task PutWithTtlAsync_WithInvalidKey_ThrowsException()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        await Assert.ThrowsAsync<NatsKVException>(() =>
            store.PutWithTtlAsync(string.Empty, "value").AsTask());
    }

    [Fact]
    public async Task PutWithTtlAsync_OverwritesExistingValue()
    {
        var key = MethodKey();
        var value1 = "first-value";
        var value2 = "second-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var revision1 = await store.PutWithTtlAsync(key, value1);
        var revision2 = await store.PutWithTtlAsync(key, value2);

        Assert.True(revision2 > revision1);

        var entry = await store.GetEntryAsync<string>(key);
        Assert.Equal(value2, entry.Value);
    }
}
