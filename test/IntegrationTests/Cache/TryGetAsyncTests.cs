using System.Buffers;
using CodeCargo.Nats.DistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class TryGetAsyncTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task TryGetAsyncLogsSwallowedExceptionAndReturnsFalse()
    {
        // Point the cache at a bucket that does not exist so the read path throws when it resolves the
        // KV store. The exception is swallowed to honor the IBufferDistributedCache contract, but it must
        // still be logged so a connectivity error is distinguishable from a normal cache miss.
        var logger = new RecordingLogger<NatsCache>();
        var cache = new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "does-not-exist" }),
            NatsConnection,
            logger);

        var destination = new ArrayBufferWriter<byte>();
        var result = await cache.TryGetAsync(MethodKey(), destination, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(0, destination.WrittenCount);
        Assert.Contains(
            logger.Records,
            r => r is { LogLevel: LogLevel.Debug, Exception: not null } && r.EventId.Name == "Exception");
    }
}
