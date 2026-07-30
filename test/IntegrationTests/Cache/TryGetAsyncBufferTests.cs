using System.Buffers;
using System.Text;
using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

// Behavioral coverage for the single-copy TryGetAsync(IBufferWriter<byte>) read path: a hit writes the exact
// payload into the caller's buffer, and every non-hit (miss, undeserializable, absolutely expired) leaves the
// buffer untouched -- the IBufferWriter "nothing written on a miss" contract.
public class TryGetAsyncBufferTests : TestBase
{
    // Held explicitly rather than captured from the primary constructor parameter, which would also be passed
    // to the base constructor and warn under CS9107 (CI builds warnings-as-errors).
    private readonly NatsIntegrationFixture _fixture;

    public TryGetAsyncBufferTests(NatsIntegrationFixture fixture)
        : base(fixture) =>
        _fixture = fixture;

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
        var timeProvider = new FakeTimeProvider();
        await using var provider = BuildProvider(timeProvider);
        var cache = (IBufferDistributedCache)provider.GetRequiredService<IDistributedCache>();

        // Five-minute absolute expiration -> five-minute real NATS TTL, so the entry is still present; the
        // cache's own clock then jumps past it. Advancing a fake clock (rather than sleeping on a ~1s real
        // TTL, which risks NATS reaping the key first and landing on NotFound) pins the expired branch.
        await cache.SetAsync(
            key,
            new byte[] { 1, 2, 3, 4 },
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)),
            Token);
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        var destination = new ArrayBufferWriter<byte>();
        var hit = await cache.TryGetAsync(key, destination, Token);

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
        var timeProvider = new FakeTimeProvider();
        await using var provider = BuildProvider(timeProvider);
        var cache = (IBufferDistributedCache)provider.GetRequiredService<IDistributedCache>();

        // Sliding renews on access, but the absolute expiration is the hard ceiling. The sliding flag drives
        // the read down the materialized branch; advancing the fake clock past the absolute instant then
        // exercises that branch's absolute-expiry eviction deterministically.
        await cache.SetAsync(
            key,
            new byte[] { 9, 8, 7 },
            new DistributedCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromMinutes(1))
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(5)),
            Token);
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        var destination = new ArrayBufferWriter<byte>();
        var hit = await cache.TryGetAsync(key, destination, Token);

        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public async Task ReturnsFalseWhenDestinationWriterFails()
    {
        var key = MethodKey();
        await DistributedCache.SetAsync(key, new byte[] { 1, 2, 3, 4 }, new DistributedCacheEntryOptions(), Token);

        // The caller's writer throws on the payload (mirroring HybridCache's quota-limited writer). TryGet
        // honors the IBufferDistributedCache contract -- it swallows and returns false rather than throwing
        // -- while the read core records the failure as an error (asserted in TelemetryTests), not a miss.
        var destination = new QuotaBufferWriter(maxLength: 0);
        var hit = await BufferCache.TryGetAsync(key, destination, Token);

        Assert.False(hit);
        Assert.Equal(0, destination.WrittenCount);
    }

    // A container mirroring TestBase's own, but with a controllable clock injected so absolute-expiry branches
    // can be reached by advancing time instead of sleeping on a short real TTL.
    private ServiceProvider BuildProvider(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        _fixture.ConfigureServices(services);
        services.AddNatsDistributedCache(options => options.BucketName = "cache");
        return services.BuildServiceProvider();
    }
}
