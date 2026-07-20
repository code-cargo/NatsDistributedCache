using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Cache;

/// <summary>
/// Telemetry behavior that can be asserted without a NATS server.
/// </summary>
/// <remarks>
/// Each test builds its own <see cref="IMeterFactory"/> so the cache's instruments are scoped to that
/// factory rather than the process-wide fallback meter. Without that, unit test classes running in
/// parallel would observe each other's measurements.
/// </remarks>
public class TelemetryUnitTests
{
    [Fact]
    public void PublicTelemetryNamesAreStable()
    {
        // These names are load-bearing: users hardcode them in AddMeter/AddSource calls and build
        // dashboards and alerts on the instrument names. This repo has no PublicAPI.Shipped.txt and no
        // API-approval test, so this is the only thing that turns a rename into a visible failure rather
        // than a silent break of every downstream dashboard.
        Assert.Equal("CodeCargo.Nats.DistributedCache", NatsCacheTelemetryNames.MeterName);
        Assert.Equal("CodeCargo.Nats.DistributedCache", NatsCacheTelemetryNames.ActivitySourceName);
        Assert.Equal("nats.cache.operation.duration", NatsCacheTelemetryNames.OperationDurationInstrumentName);
        Assert.Equal("nats.cache.misses", NatsCacheTelemetryNames.MissesInstrumentName);
    }

    [Fact]
    public void InstrumentsUseDocumentedUnits()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        using var duration = CreateDurationCollector(meterFactory);
        using var misses = CreateMissesCollector(meterFactory);

        // Creating the cache is not enough; instruments are built on first use. Drive one failing
        // operation so the meter is materialized, then assert the published metadata.
        var cache = CreateCache(meterFactory);
        Assert.ThrowsAny<Exception>(() => cache.Set(nameof(InstrumentsUseDocumentedUnits), [1], new()));

        // Seconds, not milliseconds: OpenTelemetry mandates seconds for duration histograms, and a wrong
        // unit here is invisible until it is already in someone's dashboard.
        Assert.NotNull(duration.Instrument);
        Assert.NotNull(misses.Instrument);
        Assert.Equal("s", duration.Instrument.Unit);
        Assert.Equal("{miss}", misses.Instrument.Unit);
    }

    [Fact]
    public void InvalidExpirationRecordsNoMeasurement()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var timeProvider = new FakeTimeProvider();
        var cache = CreateCache(meterFactory, timeProvider);
        using var duration = CreateDurationCollector(meterFactory);

        var expired = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(1);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(
            () => cache.Set(
                nameof(InvalidExpirationRecordsNoMeasurement),
                [1],
                new DistributedCacheEntryOptions().SetAbsoluteExpiration(expired)));

        // A caller passing a bad expiration is a caller bug that never reaches NATS. Counting it would
        // fire the error-rate alert for an operation that was never attempted, and would inject a ~0s
        // sample into the latency distribution. The scope deliberately opens after GetTtl.
        Assert.Empty(duration.GetMeasurementSnapshot());
    }

    [Fact]
    public void SyncSetOfReadOnlySequenceRecordsExactlyOneMeasurement()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var cache = CreateCache(meterFactory);
        using var duration = CreateDurationCollector(meterFactory);

        // Set(ReadOnlySequence) -> SetAsync(ReadOnlySequence) -> SetAsync(byte[]) is three deep, and is
        // how HybridCache writes to L2. Only the innermost overload is instrumented, so this must still
        // produce a single measurement.
        Assert.ThrowsAny<Exception>(
            () => cache.Set(
                nameof(SyncSetOfReadOnlySequenceRecordsExactlyOneMeasurement),
                new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }),
                new DistributedCacheEntryOptions()));

        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("set", measurement.Tags["nats.cache.operation"]);
        Assert.Equal("error", measurement.Tags["nats.cache.result"]);
    }

    [Fact]
    public void SyncGetRecordsExactlyOneMeasurementTaggedError()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var cache = CreateCache(meterFactory);
        using var duration = CreateDurationCollector(meterFactory);
        using var misses = CreateMissesCollector(meterFactory);

        Assert.ThrowsAny<Exception>(() => cache.Get(nameof(SyncGetRecordsExactlyOneMeasurementTaggedError)));

        // Get -> GetAsync -> GetAndRefreshAsync: instrumented only at the core, so the sync wrapper adds
        // no second measurement.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("get", measurement.Tags["nats.cache.operation"]);
        Assert.Equal("error", measurement.Tags["nats.cache.result"]);
        Assert.NotNull(measurement.Tags["error.type"]);

        // A failure is an error, never a miss: the misses counter must stay untouched so that hit-rate
        // dashboards are not polluted by outages.
        Assert.Empty(misses.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task RefreshTagsOperationRefreshNotGet()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var cache = CreateCache(meterFactory);
        using var duration = CreateDurationCollector(meterFactory);

        await Assert.ThrowsAnyAsync<Exception>(
            () => cache.RefreshAsync(nameof(RefreshTagsOperationRefreshNotGet), TestContext.Current.CancellationToken));

        // Refresh shares GetAndRefreshAsync with Get, so this pins that the operation discriminator is
        // threaded through correctly rather than every read reporting as "get".
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("refresh", measurement.Tags["nats.cache.operation"]);
    }

    [Fact]
    public async Task RemoveTagsOperationRemove()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var cache = CreateCache(meterFactory);
        using var duration = CreateDurationCollector(meterFactory);

        await Assert.ThrowsAnyAsync<Exception>(
            () => cache.RemoveAsync(nameof(RemoveTagsOperationRemove), TestContext.Current.CancellationToken));

        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("remove", measurement.Tags["nats.cache.operation"]);
    }

    [Fact]
    public void CacheKeyIsNeverRecordedOnMetricsEvenWhenRecordCacheKeysIsEnabled()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var cache = CreateCache(meterFactory, recordCacheKeys: true);
        using var duration = CreateDurationCollector(meterFactory);

        const string key = "user:12345:profile";
        Assert.ThrowsAny<Exception>(() => cache.Get(key));

        // RecordCacheKeys is a span-only opt-in. Keys are an unbounded dimension and a common PII
        // carrier, so they must never reach metrics regardless of the setting.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.DoesNotContain(measurement.Tags, tag => Equals(tag.Value, key));
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == "nats.cache.key");
    }

    [Fact]
    public void NoSubscriberDoesNotThrow()
    {
        using var provider = BuildMeterProvider();
        var cache = CreateCache(provider.GetRequiredService<IMeterFactory>());

        // Exercises the fast path (no MetricCollector, no ActivityListener) in CI, so a null-reference or
        // divide-by-zero in the disabled branch cannot go unnoticed.
        Assert.ThrowsAny<Exception>(() => cache.Get(nameof(NoSubscriberDoesNotThrow)));
        Assert.ThrowsAny<Exception>(() => cache.Remove(nameof(NoSubscriberDoesNotThrow)));
    }

    [Fact]
    public void CallerCancellationIsTaggedCancelledNotError()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var telemetry = new NatsCacheTelemetry(meterFactory, "cache", false);
        using var duration = CreateDurationCollector(meterFactory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var scope = NatsCacheOperationScope.Start(
            telemetry,
            TimeProvider.System,
            CacheOperation.Get,
            "key",
            cts.Token);
        scope.SetError(new OperationCanceledException(cts.Token));
        scope.Complete();

        // Cooperative shutdown is not a failure, and callers are told they may exclude it from alerting.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("cancelled", measurement.Tags["nats.cache.result"]);
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == "error.type");
    }

    [Fact]
    public void CancellationExceptionWithoutCallerCancellationIsTaggedError()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var telemetry = new NatsCacheTelemetry(meterFactory, "cache", false);
        using var duration = CreateDurationCollector(meterFactory);

        // A NATS request timeout surfaces as TaskCanceledException with the caller's token unsignalled.
        // Classifying that as `cancelled` would hide a real outage from the error rate, since callers are
        // told they can exclude cancellations from alerting. TryGetAsync draws the same line before
        // logging these at Warning, so metrics and logs must agree that this is a failure.
        var scope = NatsCacheOperationScope.Start(
            telemetry,
            TimeProvider.System,
            CacheOperation.Get,
            "key",
            CancellationToken.None);
        scope.SetError(new TaskCanceledException("NATS request timed out"));
        scope.Complete();

        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("error", measurement.Tags["nats.cache.result"]);
        Assert.Equal("System.Threading.Tasks.TaskCanceledException", measurement.Tags["error.type"]);
    }

    [Fact]
    public void MissesAreRecordedWhileTheHistogramIsDisabled()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var telemetry = new NatsCacheTelemetry(meterFactory, "cache", false);
        using var misses = CreateMissesCollector(meterFactory);

        // The precondition is what gives this test its value: only the counter is subscribed, mimicking a
        // user who dropped the histogram with an OTel View. Asserting it explicitly means the test cannot
        // silently stop covering the regression if something else starts enabling the histogram.
        Assert.False(telemetry.OperationDuration.Enabled);
        Assert.True(telemetry.Misses.Enabled);

        var scope = NatsCacheOperationScope.Start(
            telemetry,
            TimeProvider.System,
            CacheOperation.Get,
            "key",
            CancellationToken.None);
        scope.SetMiss(CacheMissReason.NotFound);
        scope.Complete();

        var miss = Assert.Single(misses.GetMeasurementSnapshot());
        Assert.Equal(1, miss.Value);
        Assert.Equal("not_found", miss.Tags["nats.cache.miss.reason"]);
    }

    [Fact]
    public void ThrowingListenerDoesNotBreakTheOperation()
    {
        using var provider = BuildMeterProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var telemetry = new NatsCacheTelemetry(meterFactory, "cache", false);

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NatsCacheTelemetryNames.ActivitySourceName,
            Sample = SampleAllData,
            ActivityStopped = _ => throw new InvalidOperationException("badly-behaved exporter"),
        };
        ActivitySource.AddActivityListener(listener);

        var scope = NatsCacheOperationScope.Start(
            telemetry,
            TimeProvider.System,
            CacheOperation.Get,
            "key",
            CancellationToken.None);
        scope.SetHit();

        // Complete() always runs from a finally block, so an exception escaping it would replace the
        // in-flight cache exception or fail an operation that actually succeeded. ActivitySource does not
        // guard listener callbacks, so a third-party exporter really can throw into this frame.
        scope.Complete();

        // And the activity is still stopped despite the throw, so Activity.Current is not left dangling —
        // otherwise every subsequent span on this context would be misparented.
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void UnsubscribedScopeAllocatesNothing()
    {
        using var provider = BuildMeterProvider();
        var telemetry = new NatsCacheTelemetry(provider.GetRequiredService<IMeterFactory>(), "cache", false);

        // Warm up so JIT compilation and any one-time initialization are not counted below.
        for (var i = 0; i < 100; i++)
        {
            RunScope(telemetry);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            RunScope(telemetry);
        }

        // Pins the README's claim that an unsubscribed cache allocates nothing per operation. With no
        // listener the scope must be a zeroed struct: no timestamp, no TagList, no Activity, no boxing.
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void RunScope(NatsCacheTelemetry telemetry)
    {
        var scope = NatsCacheOperationScope.Start(
            telemetry,
            TimeProvider.System,
            CacheOperation.Get,
            "key",
            CancellationToken.None);
        scope.SetMiss(CacheMissReason.NotFound);
        scope.Complete();
    }

    // Named static method rather than a lambda: SampleActivity<T> takes a `ref` parameter, and lambda
    // inference for ref parameters differs across the two target frameworks.
    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options) =>
        ActivitySamplingResult.AllDataAndRecorded;

    private static ServiceProvider BuildMeterProvider() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider();

    private static MetricCollector<double> CreateDurationCollector(IMeterFactory meterFactory) =>
        new(
            meterFactory,
            NatsCacheTelemetryNames.MeterName,
            NatsCacheTelemetryNames.OperationDurationInstrumentName);

    private static MetricCollector<long> CreateMissesCollector(IMeterFactory meterFactory) =>
        new(meterFactory, NatsCacheTelemetryNames.MeterName, NatsCacheTelemetryNames.MissesInstrumentName);

    // A cache over a mocked connection: every KV operation fails, which is exactly what makes the error
    // paths deterministic without a server.
    private static NatsCache CreateCache(
        IMeterFactory meterFactory,
        TimeProvider? timeProvider = null,
        bool recordCacheKeys = false)
    {
        var mockNatsConnection = new Mock<INatsConnection>();
        var opts = new NatsOpts { LoggerFactory = new LoggerFactory() };
        mockNatsConnection.SetupGet(m => m.Opts).Returns(opts);
        mockNatsConnection.SetupGet(m => m.Connection).Returns(new NatsConnection(opts));

        var options = new NatsCacheOptions { BucketName = "cache" };
        options.Telemetry.RecordCacheKeys = recordCacheKeys;

        return new NatsCache(Options.Create(options), mockNatsConnection.Object)
        {
            TimeProvider = timeProvider ?? new FakeTimeProvider(),
            MeterFactory = meterFactory,
        };
    }
}
