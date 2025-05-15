using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;
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

        // Assert - Verify the value is retrievable from hybrid cache
        var result = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(Array.Empty<byte>()));
        Assert.NotEmpty(result);
        Assert.Equal(value, result);

        // Act
        await HybridCache.RemoveAsync(key);

        // Assert - Verify the value is not stored in NATS KV store
        await Assert.ThrowsAsync<NatsKVKeyDeletedException>(async () => await kvStore.GetEntryAsync<byte[]>(key));

        // Assert - Verify the value is not retrievable from hybrid cache
        result = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(Array.Empty<byte>()));
        Assert.Empty(result);
    }

    [Fact]
    public async Task HybridCacheSerializesDateTime()
    {
        // Arrange
        var key = MethodKey();
        const string invariant = "2025-05-15T17:18:58.7503097Z";
        var date = DateTime.Parse(invariant, CultureInfo.InvariantCulture);

        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10)
        };

        // Act - Store the complex object in the cache
        await HybridCache.SetAsync(key, date, options);

        // Assert - date is serialized as expected
        var writer = new ArrayBufferWriter<byte>();
        new NatsUtf8PrimitivesSerializer<DateTime>().Serialize(writer, date);
        var serializedBytes = writer.WrittenSpan.ToArray();

        var kvStore = await NatsConnection.CreateKeyValueStoreContext().GetStoreAsync("cache");
        NatsJsonContextSerializer<CacheEntry> cacheEntrySerializer = new(CacheEntryJsonContext.Default);
        var kvEntry = await kvStore.GetEntryAsync(key, serializer: cacheEntrySerializer);
        Assert.NotNull(kvEntry.Value?.Data);

        // HybridCache adds additional data to the front of the serialized value, so we're matching only the relevant data
        var length = serializedBytes.Length;
        Assert.Equal(serializedBytes[^length], kvEntry.Value.Data[^length]);

        // Assert - date is deserialized as expected
        var retrieved = await HybridCache.GetOrCreateAsync(key, async ct => await Task.FromResult(DateTime.UnixEpoch));
        Assert.Equal(date, retrieved);

        // Cleanup
        await HybridCache.RemoveAsync(key);
    }
}
