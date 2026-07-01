using System.Buffers;
using CodeCargo.Nats.DistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class TryGetAsyncTests(NatsIntegrationFixture fixture) : TestBase(fixture)
{
    [Fact]
    public async Task TryGetAsyncSwallowsFailureAndLogsOnceAtWarning()
    {
        var cache = CreateFailingCache(out var logger);
        var destination = new ArrayBufferWriter<byte>();

        var result = await cache.TryGetAsync(MethodKey(), destination, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(0, destination.WrittenCount);

        // The failure is logged exactly once, at warning, with no redundant error-level entry.
        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.LogLevel);
        Assert.Equal("Exception", record.EventId.Name);
        Assert.NotNull(record.Exception);
    }

    [Fact]
    public async Task GetAsyncPropagatesFailureAndLogsOnceAtError()
    {
        var cache = CreateFailingCache(out var logger);

        await Assert.ThrowsAnyAsync<Exception>(
            () => cache.GetAsync(MethodKey(), TestContext.Current.CancellationToken));

        // A propagating read logs exactly once, at error, and is not double-logged by shared helpers.
        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Error, record.LogLevel);
        Assert.Equal("Exception", record.EventId.Name);
        Assert.NotNull(record.Exception);
    }

    [Fact]
    public async Task TryGetAsyncPropagatesCancellationWithoutLogging()
    {
        // Use the real bucket so the store resolves; the cancelled token then fails the read itself.
        var logger = new RecordingLogger<NatsCache>();
        var cache = new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "cache" }),
            NatsConnection,
            logger);
        var destination = new ArrayBufferWriter<byte>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancellation is not a cache failure: it propagates instead of becoming a false miss...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.TryGetAsync(MethodKey(), destination, cts.Token).AsTask());

        // ...and it is not logged as an exception (a benign "Connected" info entry may be present).
        Assert.DoesNotContain(logger.Records, r => r.EventId.Name == "Exception");
    }

    // Points a cache at a bucket that does not exist so the read path throws when it resolves the KV
    // store, without depending on the shared fixture connection being torn down.
    private NatsCache CreateFailingCache(out RecordingLogger<NatsCache> logger)
    {
        logger = new RecordingLogger<NatsCache>();
        return new NatsCache(
            Options.Create(new NatsCacheOptions { BucketName = "does-not-exist" }),
            NatsConnection,
            logger);
    }
}
