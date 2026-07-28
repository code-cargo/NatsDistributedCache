using System.Buffers;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

// Behavioral coverage for the single-copy TryGetAsync(IBufferWriter<byte>) read path: a hit writes the
// exact payload into the caller's buffer, and every non-hit (miss, undeserializable, absolutely expired)
// leaves the buffer untouched — the IBufferWriter "nothing written on a miss" contract.
public class TryGetAsyncBufferTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    // A legacy JSON envelope from a pre-binary release: the first byte is '{' (0x7B), which never matches
    // the binary FormatVersion, so the entry deserializes to a miss.
    private static readonly byte[] LegacyJsonEntry =
        Encoding.UTF8.GetBytes("{\"absexp\":null,\"sldexp\":null,\"data\":\"AQID\"}");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IBufferDistributedCache BufferCache => (IBufferDistributedCache)DistributedCache;

    [Fact]
    public async Task WritesExactPayloadOnHit()
    {
        var key = MethodKey();
        var value = Encoding.UTF8.GetBytes($"buffer-hit-{Guid.NewGuid()}");
        await DistributedCache.SetAsync(key, value, new DistributedCacheEntryOptions(), Token);

        var destination = new ArrayBufferWriter<byte>();
        var hit = await BufferCache.TryGetAsync(key, destination, Token);

        Assert.True(hit);
        Assert.Equal(value.Length, destination.WrittenCount);
        Assert.Equal(value, destination.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task EmptyPayloadIsAHitThatWritesNothing()
    {
        var key = MethodKey();
        await DistributedCache.SetAsync(key, Array.Empty<byte>(), new DistributedCacheEntryOptions(), Token);

        var destination = new ArrayBufferWriter<byte>();
        var hit = await BufferCache.TryGetAsync(key, destination, Token);

        // An empty stored value is a genuine hit (true), and correctly writes zero bytes.
        Assert.True(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public async Task WritesNothingOnMiss()
    {
        var key = MethodKey();
        await DistributedCache.RemoveAsync(key, Token); // known-empty state

        var destination = new ArrayBufferWriter<byte>();
        var hit = await BufferCache.TryGetAsync(key, destination, Token);

        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public async Task WritesNothingOnUndeserializableEntry()
    {
        var key = MethodKey();
        await WriteRawEntryAsync(key, LegacyJsonEntry);

        var destination = new ArrayBufferWriter<byte>();
        var hit = await BufferCache.TryGetAsync(key, destination, Token);

        // Undeserializable framing is a miss and must not leak any bytes into the caller's buffer.
        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public async Task WritesNothingOnAbsolutelyExpiredEntry()
    {
        var key = MethodKey();
        var value = new byte[] { 1, 2, 3, 4 };
        await DistributedCache.SetAsync(
            key,
            value,
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1.1)),
            Token);

        // Poll the buffer path until the entry lapses; the final read must be a miss with nothing written.
        ArrayBufferWriter<byte> destination;
        bool hit;
        var attempts = 0;
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5), Token);
            destination = new ArrayBufferWriter<byte>();
            hit = await BufferCache.TryGetAsync(key, destination, Token);
        }
        while (hit && ++attempts < 6);

        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public async Task SlidingExpirationHitWritesPayloadAcrossRepeatedReads()
    {
        var key = MethodKey();
        var value = Encoding.UTF8.GetBytes($"sliding-{Guid.NewGuid()}");
        await DistributedCache.SetAsync(
            key,
            value,
            new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(2)),
            Token);

        // The sliding branch materializes the entry, refreshes its TTL, and copies the payload out. Read it
        // twice (renewing each time) and confirm both hits return the exact bytes.
        for (var i = 0; i < 2; i++)
        {
            var destination = new ArrayBufferWriter<byte>();
            var hit = await BufferCache.TryGetAsync(key, destination, Token);

            Assert.True(hit);
            Assert.Equal(value, destination.WrittenSpan.ToArray());
            await Task.Delay(TimeSpan.FromSeconds(0.5), Token);
        }
    }

    [Fact]
    public async Task SlidingEntryPastAbsoluteExpirationWritesNothing()
    {
        var key = MethodKey();
        var value = new byte[] { 9, 8, 7 };

        // Sliding renews on access, but the absolute expiration is the hard ceiling: once it passes, the
        // buffer read must evict and miss, exercising the absolute-expiry branch of the sliding read path.
        await DistributedCache.SetAsync(
            key,
            value,
            new DistributedCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromSeconds(1.1))
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(2)),
            Token);

        ArrayBufferWriter<byte> destination;
        bool hit;
        var attempts = 0;
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5), Token);
            destination = new ArrayBufferWriter<byte>();
            hit = await BufferCache.TryGetAsync(key, destination, Token);
        }
        while (hit && ++attempts < 10);

        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    // Writes raw bytes at the key the cache reads, bypassing the binary serializer so the stored entry
    // cannot be deserialized.
    private async Task WriteRawEntryAsync(string key, byte[] raw)
    {
        var encodedKey = new NatsCacheKeyEncoder().Encode(key);
        var kvStore = await NatsConnection.CreateKeyValueStoreContext().GetStoreAsync("cache");
        await kvStore.PutAsync(encodedKey, raw, cancellationToken: Token);
    }
}
