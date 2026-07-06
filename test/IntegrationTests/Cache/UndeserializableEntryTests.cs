using System.Text;
using CodeCargo.Nats.DistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class UndeserializableEntryTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    // A legacy JSON envelope from a pre-binary release: the first byte is '{' (0x7B), which never
    // matches the binary FormatVersion, so the serializer returns null.
    private static readonly byte[] LegacyJsonEntry =
        Encoding.UTF8.GetBytes("{\"absexp\":null,\"sldexp\":null,\"data\":\"AQID\"}");

    [Fact]
    public async Task PresentButUndeserializableEntryIsReadAsMissAndLoggedAtDebug()
    {
        var key = MethodKey();
        var logger = new RecordingLogger<NatsCache>();
        var cache = new NatsCache(Options.Create(new NatsCacheOptions { BucketName = "cache" }), NatsConnection, logger);
        await WriteRawEntryAsync(key, LegacyJsonEntry);

        // The undeserializable entry reads as a cache miss rather than throwing...
        var result = await cache.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Null(result);

        // ...and is surfaced once at Debug to aid diagnosis without failing the caller's operation.
        var record = Assert.Single(logger.Records, r => r.EventId.Name == "UndeserializableEntry");
        Assert.Equal(LogLevel.Debug, record.LogLevel);
    }

    [Fact]
    public async Task UndeserializableEntryIsLeftInPlaceAndSelfHealsOnNextWrite()
    {
        var key = MethodKey();
        var cache = new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "cache" }),
            NatsConnection,
            new RecordingLogger<NatsCache>());
        await WriteRawEntryAsync(key, LegacyJsonEntry);

        // The read leaves the entry untouched (no eviction), so it is still a miss on a second read.
        Assert.Null(await cache.GetAsync(key, TestContext.Current.CancellationToken));

        // Writing the key (a no-TTL entry, as in the migration case) overwrites the legacy bytes...
        var value = Encoding.UTF8.GetBytes($"healed-{Guid.NewGuid()}");
        await cache.SetAsync(key, value, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        // ...and the entry is now readable, confirming the documented "re-populated on next write" path.
        Assert.Equal(value, await cache.GetAsync(key, TestContext.Current.CancellationToken));
    }

    // Writes raw bytes to the "cache" bucket at the key the cache reads, bypassing the binary
    // serializer so the stored entry cannot be deserialized.
    private async Task WriteRawEntryAsync(string key, byte[] raw)
    {
        var encodedKey = new NatsCacheKeyEncoder().Encode(key);
        var kvStore = await NatsConnection.CreateKeyValueStoreContext().GetStoreAsync("cache");
        await kvStore.PutAsync(encodedKey, raw, cancellationToken: TestContext.Current.CancellationToken);
    }
}
