using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class HybridCacheGetSetRemoveTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task HybridCacheGetSetRemoveTest()
    {
        // Arrange
        var key = MethodKey();
        var value = Encoding.UTF8.GetBytes($"test-value-{Guid.NewGuid()}");
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10)
        };

        // Assert - Verify the value is not stored in NATS KV store
        var kvStore = await NatsConnection.CreateKeyValueStoreContext().GetStoreAsync("cache");
        await Assert.ThrowsAsync<NatsKVKeyNotFoundException>(async () => await kvStore.GetEntryAsync<byte[]>(key));

        // Act
        await HybridCache.SetAsync(key, value, options);

        // Assert - Verify the value is stored in NATS KV store
        var kvEntry = await kvStore.GetEntryAsync<byte[]>(key);
        Assert.NotNull(kvEntry.Value);

        // Assert - Verify the value is retreivable from hybrid cache
        var result = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(Array.Empty<byte>()));
        Assert.NotEmpty(result);
        Assert.Equal(value, result);

        // Act
        await HybridCache.RemoveAsync(key);

        // Assert - Verify the value is not stored in NATS KV store
        await Assert.ThrowsAsync<NatsKVKeyDeletedException>(async () => await kvStore.GetEntryAsync<byte[]>(key));

        // Assert - Verify the value is not retreivable from hybrid cache
        result = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(Array.Empty<byte>()));
        Assert.Empty(result);
    }

    [Fact]
    public async Task HybridCacheSerializesDateTime()
    {
        // Arrange
        var key = MethodKey();
        var now = DateTime.UtcNow;

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10)
        };

        // Act - Store the complex object in the cache
        await HybridCache.SetAsync(key, now, options);

        // Assert - Retrieve the object and verify it matches
        var retrieved = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(DateTime.UnixEpoch));
        Assert.Equal(now, retrieved);

        // Cleanup
        await HybridCache.RemoveAsync(key);
    }
}
