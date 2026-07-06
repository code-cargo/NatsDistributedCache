using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

/// <summary>
/// Verifies opt-in bucket auto-creation (issue #38). Uses a bucket distinct from the shared "cache"
/// bucket so it never collides with <see cref="TestBase"/>'s KV_cache purge, and deletes the
/// auto-created bucket on teardown so nothing leaks across the collection lifetime.
/// </summary>
[Collection(NatsCollection.Name)]
public class BucketAutoCreationTests : IAsyncLifetime
{
    private const string BucketName = "auto-created-cache";
    private const string Key = "auto-create-key";

    private readonly NatsIntegrationFixture _fixture;
    private readonly ServiceProvider _serviceProvider;

    public BucketAutoCreationTests(NatsIntegrationFixture fixture)
    {
        _fixture = fixture;

        var services = new ServiceCollection();
        services.AddLogging();
        fixture.ConfigureServices(services);
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = BucketName;
            options.CreateBucketIfNotExists = true;

            // Memory storage keeps the test light and exercises the ConfigureBucket hook end-to-end.
            options.ConfigureBucket = cfg => cfg with { Storage = NatsKVStorageType.Memory };
        });
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FirstOperation_AutoCreatesBucket_WithHistoryOneAndLimitMarkerTtl()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = _serviceProvider.GetRequiredService<IDistributedCache>();
        var value = new byte[] { 1, 2, 3 };

        // The first cache operation triggers the lazy CreateOrUpdateStoreAsync against a missing bucket.
        await cache.SetAsync(
            Key,
            value,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
            ct);

        Assert.Equal(value, await cache.GetAsync(Key, ct));

        // The bucket now exists with the cache-required config: History = 1 and a non-zero LimitMarkerTTL.
        var kv = _fixture.NatsConnection.CreateKeyValueStoreContext();
        var status = await (await kv.GetStoreAsync(BucketName, ct)).GetStatusAsync(ct);
        Assert.Equal(BucketName, status.Bucket);
        Assert.NotEqual(TimeSpan.Zero, status.LimitMarkerTTL);
        Assert.Equal(1, status.Info.Config.MaxMsgsPerSubject); // KV History maps to MaxMsgsPerSubject
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            var kv = _fixture.NatsConnection.CreateKeyValueStoreContext();
            await kv.DeleteStoreAsync(BucketName, TestContext.Current.CancellationToken);
        }
        catch
        {
            // Best-effort cleanup; the memory-backed bucket is discarded when the server stops regardless.
        }

        await _serviceProvider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
