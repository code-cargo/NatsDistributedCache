using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Extensions;

/// <summary>
/// Integration tests for NatsExtensions.UpdateWithTtlAsync
/// </summary>
public class NatsExtensionsUpdateWithTtlTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task UpdateWithTtlAsync_UpdatesValue()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with that revision
        var newRevision = await store.UpdateWithTtlAsync(key, updatedValue, revision, TimeSpan.Zero);

        Assert.True(newRevision > revision);

        var entry = await store.GetEntryAsync<string>(key);
        Assert.Equal(updatedValue, entry.Value);
    }

    [Fact]
    public async Task UpdateWithTtlAsync_WithTtl_ExpiresAfterTtl()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value-ttl";
        var ttl = TimeSpan.FromSeconds(2);

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with TTL
        await store.UpdateWithTtlAsync(key, updatedValue, revision, ttl);

        // Value should exist immediately
        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(updatedValue, entry.Value.Value);

        // Wait for TTL to expire
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Value should be expired
        var expiredEntry = await store.TryGetEntryAsync<string>(key);
        Assert.False(expiredEntry.Success);
    }

    [Fact]
    public async Task UpdateWithTtlAsync_WithZeroTtl_DoesNotExpire()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value-no-ttl";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update without TTL
        await store.UpdateWithTtlAsync(key, updatedValue, revision, TimeSpan.Zero);

        // Value should exist immediately
        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(updatedValue, entry.Value.Value);

        // Wait a bit
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Value should still exist
        var stillExists = await store.TryGetEntryAsync<string>(key);
        Assert.True(stillExists.Success);
        Assert.Equal(updatedValue, stillExists.Value.Value);
    }

    [Fact]
    public async Task UpdateWithTtlAsync_WithWrongRevision_ThrowsException()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with wrong revision
        await Assert.ThrowsAsync<NatsKVWrongLastRevisionException>(() =>
            store.UpdateWithTtlAsync(key, updatedValue, revision + 1, TimeSpan.Zero).AsTask());
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithWrongRevision_ReturnsError()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with wrong revision
        var result = await store.TryUpdateWithTtlAsync(key, updatedValue, revision + 1, TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.IsType<NatsKVWrongLastRevisionException>(result.Error);
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithEmptyKey_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryUpdateWithTtlAsync(string.Empty, "value", 1, TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("empty", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithKeyStartingWithPeriod_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryUpdateWithTtlAsync(".invalid-key", "value", 1, TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("period", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithKeyEndingWithPeriod_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryUpdateWithTtlAsync("invalid-key.", "value", 1, TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("period", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithInvalidCharacters_ReturnsError()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        var result = await store.TryUpdateWithTtlAsync("invalid key with spaces", "value", 1, TimeSpan.Zero);

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Contains("invalid", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryUpdateWithTtlAsync_WithValidKey_ReturnsSuccess()
    {
        var key = MethodKey();
        var initialValue = "initial-value";
        var updatedValue = "updated-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with valid key
        var result = await store.TryUpdateWithTtlAsync(key, updatedValue, revision, TimeSpan.Zero);

        Assert.True(result.Success);
        Assert.True(result.Value > revision);
    }

    [Theory]
    [InlineData("simple-key")]
    [InlineData("key_with_underscore")]
    [InlineData("key/with/slashes")]
    [InlineData("key.with.periods")]
    [InlineData("key=with=equals")]
    [InlineData("MixedCase123")]
    public async Task TryUpdateWithTtlAsync_WithValidKeyFormats_Succeeds(string key)
    {
        var initialValue = "initial-value";
        var updatedValue = "updated-value";

        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        // Setup: Put initial value to get revision
        var revision = await store.PutAsync(key, initialValue);

        // Test: Update with valid key format
        var result = await store.TryUpdateWithTtlAsync(key, updatedValue, revision, TimeSpan.Zero);

        Assert.True(result.Success);

        var entry = await store.TryGetEntryAsync<string>(key);
        Assert.True(entry.Success);
        Assert.Equal(updatedValue, entry.Value.Value);
    }

    [Fact]
    public async Task UpdateWithTtlAsync_WithInvalidKey_ThrowsException()
    {
        var kvContext = NatsConnection.CreateKeyValueStoreContext();
        var store = await kvContext.GetStoreAsync("cache");

        await Assert.ThrowsAsync<NatsKVException>(() =>
            store.UpdateWithTtlAsync(string.Empty, "value", 1, TimeSpan.Zero).AsTask());
    }
}
