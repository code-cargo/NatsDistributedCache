using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Cache;

public class BucketConfigUnitTests
{
    private const string BucketName = "cache";

    [Fact]
    public void BuildBucketConfig_SetsBucketName()
    {
        var config = CreateCache().BuildBucketConfig();

        Assert.Equal(BucketName, config.Bucket);
    }

    [Fact]
    public void BuildBucketConfig_DefaultsHistoryToOne()
    {
        var config = CreateCache().BuildBucketConfig();

        Assert.Equal(1, config.History);
    }

    [Fact]
    public void BuildBucketConfig_SetsNonZeroLimitMarkerTtl()
    {
        var config = CreateCache().BuildBucketConfig();

        Assert.NotEqual(TimeSpan.Zero, config.LimitMarkerTTL);
        Assert.Equal(TimeSpan.FromSeconds(1), config.LimitMarkerTTL);
    }

    [Fact]
    public void BuildBucketConfig_InvokesConfigureBucketHook()
    {
        var config = CreateCache(o => o.ConfigureBucket = cfg => cfg with
        {
            Storage = NatsKVStorageType.Memory,
            NumberOfReplicas = 3,
        }).BuildBucketConfig();

        Assert.Equal(NatsKVStorageType.Memory, config.Storage);
        Assert.Equal(3, config.NumberOfReplicas);
    }

    [Fact]
    public void BuildBucketConfig_HookCanOverrideDefaults()
    {
        var config = CreateCache(o => o.ConfigureBucket = cfg => cfg with
        {
            History = 5,
            LimitMarkerTTL = TimeSpan.FromSeconds(30),
        }).BuildBucketConfig();

        // The hook runs after the defaults, so it wins (documented as caller responsibility for TTL correctness).
        Assert.Equal(5, config.History);
        Assert.Equal(TimeSpan.FromSeconds(30), config.LimitMarkerTTL);
    }

    [Fact]
    public void BuildBucketConfig_ReassertsBucketName_WhenHookChangesIt()
    {
        // The hook must not be able to retarget creation to a different bucket than the cache reads from.
        var config = CreateCache(o => o.ConfigureBucket = cfg => cfg with { Bucket = "some-other-bucket" })
            .BuildBucketConfig();

        Assert.Equal(BucketName, config.Bucket);
    }

    [Fact]
    public void BuildBucketConfig_ThrowsClearException_WhenHookReturnsNull()
    {
        var cache = CreateCache(o => o.ConfigureBucket = _ => null!);

        var ex = Assert.Throws<InvalidOperationException>(() => cache.BuildBucketConfig());
        Assert.Contains(nameof(NatsCacheOptions.ConfigureBucket), ex.Message);
    }

    // BuildBucketConfig never touches the connection, so a bare mock is sufficient and no server is needed.
    private static NatsCache CreateCache(Action<NatsCacheOptions>? configure = null)
    {
        var options = new NatsCacheOptions { BucketName = BucketName, CreateBucketIfNotExists = true };
        configure?.Invoke(options);
        return new NatsCache(Options.Create(options), new Mock<INatsConnection>().Object);
    }
}
