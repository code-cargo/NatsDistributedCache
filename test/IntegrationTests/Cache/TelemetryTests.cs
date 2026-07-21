using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using CodeCargo.Nats.DistributedCache.TestUtils;
using CodeCargo.Nats.DistributedCache.TestUtils.Services.Diagnostics;
using CodeCargo.Nats.DistributedCache.TestUtils.Services.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache.IntegrationTests.Cache;

public class TelemetryTests : TestBase
{
    // A legacy JSON envelope from a pre-binary release: the first byte is '{' (0x7B), which never matches
    // the binary FormatVersion, so the serializer returns null and the read is an undeserializable miss.
    private static readonly byte[] LegacyJsonEntry =
        Encoding.UTF8.GetBytes("{\"absexp\":null,\"sldexp\":null,\"data\":\"AQID\"}");

    // Held explicitly rather than captured from a primary constructor parameter: capturing a value that is
    // also passed to the base constructor warns under CS9107, and CI builds with warnings as errors.
    private readonly NatsIntegrationFixture _fixture;

    public TelemetryTests(NatsIntegrationFixture fixture)
        : base(fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task SetGetRemoveGetEmitsExpectedResultSequence()
    {
        var key = MethodKey();
        using var duration = CreateDurationCollector();
        using var misses = CreateMissesCollector();
        var token = TestContext.Current.CancellationToken;

        await DistributedCache.SetAsync(key, [1, 2, 3], new DistributedCacheEntryOptions(), token);
        Assert.NotNull(await DistributedCache.GetAsync(key, token));
        await DistributedCache.RemoveAsync(key, token);
        Assert.Null(await DistributedCache.GetAsync(key, token));

        var results = duration.GetMeasurementSnapshot()
            .Select(m => (Operation: m.Tags["nats.cache.operation"], Result: m.Tags["nats.cache.result"]))
            .ToArray();
        Assert.Equal(
            [("set", "ok"), ("get", "hit"), ("remove", "ok"), ("get", "miss")],
            results);

        // Every duration sample is a real elapsed time in seconds, not a zero or a millisecond count.
        Assert.All(duration.GetMeasurementSnapshot(), m => Assert.InRange(m.Value, 0d, 60d));

        // The trailing miss is attributed to an absent key, and the hit contributes nothing to the counter.
        var miss = Assert.Single(misses.GetMeasurementSnapshot());
        Assert.Equal(1, miss.Value);
        Assert.Equal("get", miss.Tags["nats.cache.operation"]);
        Assert.Equal("not_found", miss.Tags["nats.cache.miss.reason"]);
    }

    [Fact]
    public async Task ExpiredEntryReadTagsMissReasonExpiredAndRecordsNoRemoveOperation()
    {
        var key = MethodKey();
        var timeProvider = new FakeTimeProvider();
        await using var provider = BuildProvider(timeProvider);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var token = TestContext.Current.CancellationToken;

        // A five-minute absolute expiration gives the NATS entry a five-minute real-time TTL, then the
        // cache's own clock jumps past it. The entry is therefore still present in the bucket but
        // absolutely expired, which is the only way to reach the `expired` branch deterministically.
        await cache.SetAsync(
            key,
            [1, 2, 3],
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)),
            token);

        using var duration = CreateDurationCollector(meterFactory);
        using var misses = CreateMissesCollector(meterFactory);
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(await cache.GetAsync(key, token));

        var miss = Assert.Single(misses.GetMeasurementSnapshot());
        Assert.Equal("expired", miss.Tags["nats.cache.miss.reason"]);

        // Regression guard for the seam choice: reading an absolutely-expired entry evicts it via
        // RemoveCoreAsync, which is deliberately NOT instrumented. If instrumentation ever moves down to
        // the core, every expired read would emit a phantom operation=remove, inflating remove rate and
        // nesting a bogus remove span inside the get.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("get", measurement.Tags["nats.cache.operation"]);
        Assert.Equal("miss", measurement.Tags["nats.cache.result"]);
    }

    [Fact]
    public async Task UndeserializableEntryTagsMissReasonUndeserializable()
    {
        var key = MethodKey();
        var cache = CreateDirectCache();
        await WriteRawEntryAsync(key, LegacyJsonEntry);
        using var misses = CreateFallbackMissesCollector();

        Assert.Null(await cache.GetAsync(key, TestContext.Current.CancellationToken));

        // Distinguishes a legacy/corrupt envelope from an absent key, which is the signal operators need
        // to tell whether a JSON->binary rollout has drained.
        var miss = Assert.Single(misses.GetMeasurementSnapshot());
        Assert.Equal("undeserializable", miss.Tags["nats.cache.miss.reason"]);
    }

    [Fact]
    public async Task BufferOverloadsRecordExactlyOneMeasurementEach()
    {
        var key = MethodKey();
        var bufferCache = (IBufferDistributedCache)DistributedCache;
        using var duration = CreateDurationCollector();
        var token = TestContext.Current.CancellationToken;

        // These are precisely the two overloads HybridCache drives, and the two most at risk of double
        // counting: Set(ReadOnlySequence) is three delegations deep, and TryGetAsync wraps the read core.
        await bufferCache.SetAsync(
            key,
            new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }),
            new DistributedCacheEntryOptions(),
            token);

        var destination = new ArrayBufferWriter<byte>();
        Assert.True(await bufferCache.TryGetAsync(key, destination, token));

        var results = duration.GetMeasurementSnapshot()
            .Select(m => (Operation: m.Tags["nats.cache.operation"], Result: m.Tags["nats.cache.result"]))
            .ToArray();
        Assert.Equal([("set", "ok"), ("get", "hit")], results);
    }

    [Fact]
    public void SyncOverloadsRecordExactlyOneMeasurementEach()
    {
        var key = MethodKey();
        using var duration = CreateDurationCollector();

        // The sync wrappers delegate to the async implementations rather than carrying their own
        // instrumentation, so each must still produce exactly one measurement.
        DistributedCache.Set(key, [1, 2, 3], new DistributedCacheEntryOptions());
        Assert.NotNull(DistributedCache.Get(key));
        DistributedCache.Refresh(key);
        DistributedCache.Remove(key);

        var results = duration.GetMeasurementSnapshot()
            .Select(m => (Operation: m.Tags["nats.cache.operation"], Result: m.Tags["nats.cache.result"]))
            .ToArray();
        Assert.Equal([("set", "ok"), ("get", "hit"), ("refresh", "hit"), ("remove", "ok")], results);
    }

    [Fact]
    public async Task SwallowedTryGetFailureRecordsErrorNotMiss()
    {
        var cache = CreateFailingCache();
        var destination = new ArrayBufferWriter<byte>();
        using var duration = CreateFallbackDurationCollector();
        using var misses = CreateFallbackMissesCollector();

        Assert.False(await cache.TryGetAsync(MethodKey(), destination, TestContext.Current.CancellationToken));

        // TryGet swallows the failure and returns false to honor the IBufferDistributedCache contract, but
        // telemetry must not report it as a cache miss — that would make an outage look like a cold cache.
        // Callers wanting the observed miss rate should compute miss + error.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("get", measurement.Tags["nats.cache.operation"]);
        Assert.Equal("error", measurement.Tags["nats.cache.result"]);
        Assert.NotNull(measurement.Tags["error.type"]);
        Assert.Empty(misses.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task HybridCacheL2OperationsEmitNatsCacheInstruments()
    {
        var key = MethodKey();
        var token = TestContext.Current.CancellationToken;
        var options = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) };

        // HybridCache writes L2 through IBufferDistributedCache.SetAsync(ReadOnlySequence) — three
        // delegations deep, and the chain most at risk of double counting. SetAsync is used rather than
        // GetOrCreateAsync because the latter writes L2 in the background, which would race the assertion.
        using (var duration = CreateDurationCollector())
        {
            await HybridCache.SetAsync(key, "value", options, cancellationToken: token);

            // Asserts the count of writes rather than the full operation sequence: HybridCache may also
            // read L2 as part of a write, and pinning its exact internal choreography would make this test
            // fail on a HybridCache upgrade for no good reason. The property that matters here is that the
            // three-deep ReadOnlySequence chain records one measurement, not two.
            var write = Assert.Single(
                duration.GetMeasurementSnapshot(),
                m => Equals(m.Tags["nats.cache.operation"], "set"));
            Assert.Equal("ok", write.Tags["nats.cache.result"]);
        }

        // A second container has a cold L1, so its GetOrCreateAsync must fall through to L2 — exercising
        // TryGetAsync(IBufferWriter), the read chain HybridCache uses exclusively. This also confirms
        // AddNatsHybridCache inherits telemetry with no extra wiring.
        await using var other = BuildProvider();
        var otherHybridCache = other.GetRequiredService<HybridCache>();
        using var reads = CreateDurationCollector(other.GetRequiredService<IMeterFactory>());

        var value = await otherHybridCache.GetOrCreateAsync(
            key,
            _ => ValueTask.FromResult("factory-should-not-run"),
            options,
            cancellationToken: token);

        Assert.Equal("value", value);

        var read = Assert.Single(
            reads.GetMeasurementSnapshot(),
            m => Equals(m.Tags["nats.cache.operation"], "get"));
        Assert.Equal("hit", read.Tags["nats.cache.result"]);
    }

    [Fact]
    public async Task CallerCancellationIsTaggedCancelledEndToEnd()
    {
        // Uses the real bucket so the store resolves; the pre-cancelled token then fails the read itself.
        var cache = CreateDirectCache();
        using var duration = CreateFallbackDurationCollector();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetAsync(MethodKey(), cts.Token));

        // The caller's own cancellation is not a cache failure, so it must not land in the error rate.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal("cancelled", measurement.Tags["nats.cache.result"]);
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == "error.type");
    }

    [Fact]
    public async Task MissesAreRecordedWhenOnlyTheCounterIsSubscribed()
    {
        var key = MethodKey();

        // Subscribe to the misses counter but NOT the duration histogram, mimicking a user who drops the
        // histogram with an OTel View to cut cost. An earlier revision gated all metric work on the
        // histogram being enabled, which silently produced zero miss data in exactly this configuration.
        using var misses = CreateMissesCollector();

        Assert.Null(await DistributedCache.GetAsync(key, TestContext.Current.CancellationToken));

        var miss = Assert.Single(misses.GetMeasurementSnapshot());
        Assert.Equal(1, miss.Value);
        Assert.Equal("not_found", miss.Tags["nats.cache.miss.reason"]);
    }

    [Fact]
    public async Task SeparateServiceProvidersHaveIsolatedMeters()
    {
        var token = TestContext.Current.CancellationToken;
        await using var other = BuildProvider();
        using var thisProviderMetrics = CreateDurationCollector();
        using var otherProviderMetrics = CreateDurationCollector(other.GetRequiredService<IMeterFactory>());

        await DistributedCache.SetAsync(MethodKey(), [1], new DistributedCacheEntryOptions(), token);

        // This is the property that justifies resolving IMeterFactory instead of using a single static
        // Meter: each container aggregates independently. With a static meter the second collector would
        // observe the first provider's traffic, silently cross-contaminating any parallel test or any host
        // that builds more than one container in a process.
        Assert.Single(thisProviderMetrics.GetMeasurementSnapshot());
        Assert.Empty(otherProviderMetrics.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task ReadProducesOneClientSpanCarryingResultAndBucket()
    {
        var key = MethodKey();
        using var activities = new RecordingActivityListener(NatsCacheTelemetryNames.ActivitySourceName);

        Assert.Null(await DistributedCache.GetAsync(key, TestContext.Current.CancellationToken));

        var activity = Assert.Single(activities.Activities);
        Assert.Equal("get cache", activity.DisplayName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("get", activity.GetTagItem("nats.cache.operation"));
        Assert.Equal("cache", activity.GetTagItem("nats.cache.bucket"));
        Assert.Equal("miss", activity.GetTagItem("nats.cache.result"));
        Assert.Equal("not_found", activity.GetTagItem("nats.cache.miss.reason"));

        // A miss is a normal outcome, so the span status stays Unset. Marking misses as errors would make
        // every cold cache look like an outage in a trace UI.
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);

        // Keys are omitted from spans unless RecordCacheKeys is enabled, because they commonly carry PII.
        Assert.Null(activity.GetTagItem("nats.cache.key"));
    }

    [Fact]
    public async Task FailedReadProducesSpanWithErrorStatusAndErrorType()
    {
        var cache = CreateFailingCache();
        using var activities = new RecordingActivityListener(NatsCacheTelemetryNames.ActivitySourceName);

        await Assert.ThrowsAnyAsync<Exception>(
            () => cache.GetAsync(MethodKey(), TestContext.Current.CancellationToken));

        var activity = Assert.Single(activities.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("error", activity.GetTagItem("nats.cache.result"));
        Assert.NotNull(activity.GetTagItem("error.type"));
    }

    [Fact]
    public async Task RecordCacheKeysAddsKeyToSpanOnly()
    {
        var key = MethodKey();
        var options = new NatsCacheOptions { BucketName = "cache" };
        options.Telemetry.RecordCacheKeys = true;
        var cache = new NatsCache(Options.Create(options), NatsConnection);

        using var activities = new RecordingActivityListener(NatsCacheTelemetryNames.ActivitySourceName);
        using var duration = CreateFallbackDurationCollector();

        Assert.Null(await cache.GetAsync(key, TestContext.Current.CancellationToken));

        var activity = Assert.Single(activities.Activities);
        Assert.Equal(key, activity.GetTagItem("nats.cache.key"));

        // Opt-in or not, the key never reaches metrics: it is an unbounded dimension that would break
        // cardinality for every consumer.
        var measurement = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.DoesNotContain(measurement.Tags, tag => Equals(tag.Value, key));
    }

    // Collectors for directly-constructed caches, which have no IMeterFactory and therefore publish on the
    // process-wide fallback meter (null scope). Safe to assert on because integration tests share a
    // collection and run serially.
    private static MetricCollector<double> CreateFallbackDurationCollector() =>
        new(null, NatsCacheTelemetryNames.MeterName, NatsCacheTelemetryNames.OperationDurationInstrumentName);

    private static MetricCollector<long> CreateFallbackMissesCollector() =>
        new(null, NatsCacheTelemetryNames.MeterName, NatsCacheTelemetryNames.MissesInstrumentName);

    // Collectors for caches built by this test's own container. Scoping to the meter factory keeps the
    // measurements isolated from any cache using the process-wide fallback meter.
    private MetricCollector<double> CreateDurationCollector(IMeterFactory? meterFactory = null) =>
        new(
            meterFactory ?? MeterFactory,
            NatsCacheTelemetryNames.MeterName,
            NatsCacheTelemetryNames.OperationDurationInstrumentName);

    private MetricCollector<long> CreateMissesCollector(IMeterFactory? meterFactory = null) =>
        new(
            meterFactory ?? MeterFactory,
            NatsCacheTelemetryNames.MeterName,
            NatsCacheTelemetryNames.MissesInstrumentName);

    // A container mirroring TestBase's, optionally on a fake clock, used by tests that need a second
    // isolated meter or a controllable clock.
    private ServiceProvider BuildProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        _fixture.ConfigureServices(services);
        services.AddHybridCacheTestClient();
        return services.BuildServiceProvider();
    }

    // Directly constructed, so it uses the process-wide fallback meter rather than this test's factory.
    // Safe because integration tests share a collection and therefore run serially.
    private NatsCache CreateDirectCache() =>
        new(Options.Create(new NatsCacheOptions { BucketName = "cache" }), NatsConnection);

    // Points a cache at a bucket that does not exist so the read path throws when it resolves the KV store.
    private NatsCache CreateFailingCache() =>
        new(
            Options.Create(new NatsCacheOptions { BucketName = "does-not-exist" }),
            NatsConnection,
            new RecordingLogger<NatsCache>());

    private async Task WriteRawEntryAsync(string key, byte[] raw)
    {
        var encodedKey = new NatsCacheKeyEncoder().Encode(key);
        var kvStore = await NatsConnection.CreateKeyValueStoreContext().GetStoreAsync("cache");
        await kvStore.PutAsync(encodedKey, raw, cancellationToken: TestContext.Current.CancellationToken);
    }
}
