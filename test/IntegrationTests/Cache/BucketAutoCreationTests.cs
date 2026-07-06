using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

/// <summary>
/// Verifies opt-in bucket auto-creation (issue #38). Uses buckets distinct from the shared "cache"
/// bucket so they never collide with <see cref="TestBase"/>'s KV_cache purge, and deletes any
/// bucket it touches on teardown so nothing leaks across the collection lifetime.
/// </summary>
[Collection(NatsCollection.Name)]
public class BucketAutoCreationTests(NatsIntegrationFixture fixture) : IAsyncLifetime
{
    private const string Key = "auto-create-key";

    private readonly List<ServiceProvider> _serviceProviders = new();
    private readonly List<string> _bucketsToDelete = new();

    [Fact]
    public async Task FirstOperation_CreatesMissingBucket_WithHistoryOneAndLimitMarkerTtl()
    {
        const string bucketName = "auto-created-cache";
        var ct = TestContext.Current.CancellationToken;

        // Memory storage keeps the test light and exercises the ConfigureBucket hook end-to-end.
        var cache = BuildCache(bucketName, cfg => cfg with { Storage = NatsKVStorageType.Memory });
        var value = new byte[] { 1, 2, 3 };

        // The first cache operation triggers creation of the missing bucket.
        await cache.SetAsync(
            Key,
            value,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
            ct);

        Assert.Equal(value, await cache.GetAsync(Key, ct));

        // The bucket now exists with the cache-required config: History = 1 and a non-zero LimitMarkerTTL.
        var status = await GetStatusAsync(bucketName, ct);
        Assert.Equal(bucketName, status.Bucket);
        Assert.NotEqual(TimeSpan.Zero, status.LimitMarkerTTL);
        Assert.Equal(1, status.Info.Config.MaxMsgsPerSubject); // KV History maps to MaxMsgsPerSubject
    }

    [Fact]
    public async Task ExistingBucket_IsUsedAsIs_AndNeverModified()
    {
        const string bucketName = "operator-managed-cache";
        var ct = TestContext.Current.CancellationToken;

        // Operator pre-creates the bucket with a distinctive LimitMarkerTTL (5s).
        var kv = fixture.NatsConnection.CreateKeyValueStoreContext();
        _bucketsToDelete.Add(bucketName);
        await kv.CreateStoreAsync(
            new NatsKVConfig(bucketName)
            {
                History = 1,
                LimitMarkerTTL = TimeSpan.FromSeconds(5),
                Storage = NatsKVStorageType.Memory,
            },
            ct);

        // Enable auto-create with a hook that WOULD set a different LimitMarkerTTL (1s) if it created the bucket.
        var cache = BuildCache(
            bucketName,
            cfg => cfg with { LimitMarkerTTL = TimeSpan.FromSeconds(1), Storage = NatsKVStorageType.Memory },
            trackForDeletion: false);

        await cache.SetAsync(
            Key,
            new byte[] { 9 },
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
            ct);

        // The pre-existing bucket must be left untouched: LimitMarkerTTL is still the operator's 5s, not 1s.
        var status = await GetStatusAsync(bucketName, ct);
        Assert.Equal(TimeSpan.FromSeconds(5), status.LimitMarkerTTL);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var kv = fixture.NatsConnection.CreateKeyValueStoreContext();
        foreach (var bucket in _bucketsToDelete)
        {
            try
            {
                await kv.DeleteStoreAsync(bucket, TestContext.Current.CancellationToken);
            }
            catch
            {
                // Best-effort cleanup; memory-backed buckets are discarded when the server stops regardless.
            }
        }

        foreach (var sp in _serviceProviders)
        {
            await sp.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private IDistributedCache BuildCache(
        string bucketName,
        Func<NatsKVConfig, NatsKVConfig> configureBucket,
        bool trackForDeletion = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        fixture.ConfigureServices(services);
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = bucketName;
            options.CreateBucketIfNotExists = true;
            options.ConfigureBucket = configureBucket;
        });

        var serviceProvider = services.BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);
        if (trackForDeletion)
        {
            _bucketsToDelete.Add(bucketName);
        }

        return serviceProvider.GetRequiredService<IDistributedCache>();
    }

    private async Task<NatsKVStatus> GetStatusAsync(string bucketName, CancellationToken ct)
    {
        var kv = fixture.NatsConnection.CreateKeyValueStoreContext();
        return await (await kv.GetStoreAsync(bucketName, ct)).GetStatusAsync(ct);
    }
}
