using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Cache;

public class PurgeByPrefixUnitTests
{
    private const string BucketName = "cache";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task PurgeByPrefixAsync_RejectsEmptyWhitespaceOrDotOnlyPrefix(string? prefix)
    {
        // The guard runs before the KV store is resolved, so a bare mock connection is enough (no server
        // needed). Rejecting these values prevents a scoped purge from collapsing into a purge of the whole
        // configured-prefix space (or, with no CacheKeyPrefix, the entire bucket).
        var cache = CreateCache();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => cache.PurgeByPrefixAsync(prefix!));
        Assert.Equal("prefix", ex.ParamName);
    }

    // The guard never touches the connection, so a bare mock is sufficient and no server is needed.
    private static NatsCache CreateCache() =>
        new(Options.Create(new NatsCacheOptions { BucketName = BucketName }), new Mock<INatsConnection>().Object);
}
