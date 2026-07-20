using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeCargo.Nats.DistributedCache;

// The logical cache operation a telemetry scope covers. Deliberately coarser than the public API surface:
// every read path (Get, TryGet, and their sync overloads) reports as Get, so hit ratio is computed over
// all reads. TryGet's IBufferWriter overload is a zero-copy detail, not a different cache operation.
internal enum CacheOperation
{
    Get,
    Set,
    Refresh,
    Remove,
}

// Why a read returned no data. A closed set of four values, one per `return null` site in
// GetAndRefreshAsync, so it is safe as a metric dimension. Never derived from data or exception text.
internal enum CacheMissReason
{
    NotFound,
    Expired,
    Undeserializable,
    RevisionConflict,
}

// Every tag key and tag value, as compile-time constants. Keeping them all in one place means metric
// cardinality is provable by inspecting this class, and no string is allocated at record time.
internal static class TelemetryTags
{
    internal const string Operation = "nats.cache.operation";
    internal const string Result = "nats.cache.result";
    internal const string MissReason = "nats.cache.miss.reason";
    internal const string Bucket = "nats.cache.bucket";
    internal const string Key = "nats.cache.key";

    // The stable OpenTelemetry registry attribute, deliberately reused rather than namespaced under
    // nats.cache.* so standard cross-library error drilldowns work against our data unmodified.
    internal const string ErrorType = "error.type";

    internal const string OperationGet = "get";
    internal const string OperationSet = "set";
    internal const string OperationRefresh = "refresh";
    internal const string OperationRemove = "remove";

    internal const string ResultHit = "hit";
    internal const string ResultMiss = "miss";
    internal const string ResultOk = "ok";
    internal const string ResultError = "error";
    internal const string ResultCancelled = "cancelled";

    internal const string MissReasonNotFound = "not_found";
    internal const string MissReasonExpired = "expired";
    internal const string MissReasonUndeserializable = "undeserializable";
    internal const string MissReasonRevisionConflict = "revision_conflict";

    // Every member is listed explicitly and the fallback throws rather than returning a plausible value:
    // a silent default would let a newly added CacheOperation be mislabelled as an existing one, which is
    // invisible in a dashboard. The `_` arm exists only to satisfy exhaustiveness checking.
    internal static string Name(CacheOperation operation) => operation switch
    {
        CacheOperation.Get => OperationGet,
        CacheOperation.Set => OperationSet,
        CacheOperation.Refresh => OperationRefresh,
        CacheOperation.Remove => OperationRemove,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    internal static string Name(CacheMissReason reason) => reason switch
    {
        CacheMissReason.NotFound => MissReasonNotFound,
        CacheMissReason.Expired => MissReasonExpired,
        CacheMissReason.Undeserializable => MissReasonUndeserializable,
        CacheMissReason.RevisionConflict => MissReasonRevisionConflict,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

// Per-cache-instance instrument bundle.
//
// Instrument naming: there is no stable OpenTelemetry semantic convention for caches, so nats.cache.* is a
// library-scoped prefix (the pattern FusionCache uses with fusioncache.*). db.client.* was considered and
// rejected: NATS KV has no registered db.system.name value, the DB conventions cannot express a cache
// hit/miss, and emitting it would merge cache traffic into every user's database dashboards and SLOs. A
// bare cache.* was rejected as squatting on a namespace OTel may later stabilize with different semantics.
// Either convention can be added alongside nats.cache.* later without breaking existing dashboards.
internal sealed class NatsCacheTelemetry
{
    // Instrumentation scope version reported to listeners. Must be declared before the fields below, whose
    // initializers consume it — static field initializers run in textual order.
    internal static readonly string TelemetryVersion =
        typeof(NatsCacheTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    // The ActivitySource is static while the Meter is per-instance, and that asymmetry is deliberate.
    // There is no IActivitySourceFactory in the BCL, and traces have no aggregation-identity problem: an
    // ActivityListener filters by source *name* and each Activity is sampled and exported independently.
    // Metrics do — MeterProvider aggregates by meter identity, so two hosts in one process need distinct
    // Meter instances to avoid cross-contamination. Please don't "fix" this into symmetry.
    internal static readonly ActivitySource ActivitySource =
        new(NatsCacheTelemetryNames.ActivitySourceName, TelemetryVersion);

    // Fallback for direct (non-DI) construction, and for DI containers without AddMetrics(). Process-wide
    // and never disposed; subscribers cannot distinguish it from a factory-created meter of the same name.
    private static readonly Meter SharedMeter = new(NatsCacheTelemetryNames.MeterName, TelemetryVersion);

    private readonly string _getSpanName;
    private readonly string _setSpanName;
    private readonly string _refreshSpanName;
    private readonly string _removeSpanName;

    internal NatsCacheTelemetry(IMeterFactory? meterFactory, string bucketName, bool recordCacheKeys)
    {
        // Never disposed either way: a factory-created meter is owned by the container, and the shared one
        // lives for the process. This is what keeps NatsCache out of IDisposable.
        //
        // IMeterFactory.Create caches by name+version, so two NatsCache instances sharing a container get
        // the same Meter and each register their own Instrument objects under the same identity. That is
        // benign: consumers aggregate by instrument identity plus tags, and every instrument here carries
        // nats.cache.bucket, so the two caches still resolve to distinct series.
        var meter = meterFactory?.Create(NatsCacheTelemetryNames.MeterName, TelemetryVersion) ?? SharedMeter;

        BucketName = bucketName;
        RecordCacheKeys = recordCacheKeys;

        OperationDuration = meter.CreateHistogram<double>(
            NatsCacheTelemetryNames.OperationDurationInstrumentName,
            unit: "s",
            description: "Duration of NATS distributed cache operations.");

        // No companion operations counter: the histogram's count already yields operation rate, hit ratio,
        // and error rate via the result tag, and a counter duplicating a histogram's count is an OTel
        // anti-pattern (the HTTP semantic conventions deleted exactly such a counter).
        Misses = meter.CreateCounter<long>(
            NatsCacheTelemetryNames.MissesInstrumentName,
            unit: "{miss}",
            description: "Number of NATS distributed cache read misses, by reason.");

        // Span names are precomputed per instance so the traced path allocates no strings. Passed to
        // StartActivity rather than assigned to DisplayName afterwards, because samplers see the name given
        // to StartActivity.
        _getSpanName = $"{TelemetryTags.OperationGet} {bucketName}";
        _setSpanName = $"{TelemetryTags.OperationSet} {bucketName}";
        _refreshSpanName = $"{TelemetryTags.OperationRefresh} {bucketName}";
        _removeSpanName = $"{TelemetryTags.OperationRemove} {bucketName}";
    }

    internal Histogram<double> OperationDuration { get; }

    internal Counter<long> Misses { get; }

    internal string BucketName { get; }

    internal bool RecordCacheKeys { get; }

    // Resolved by a switch rather than indexing an array by enum ordinal: a positional array silently
    // mislabels every span if a CacheOperation member is ever inserted or reordered, and it would drift
    // out of sync with TelemetryTags.Name, which supplies the matching metric tag.
    internal string SpanName(CacheOperation operation) => operation switch
    {
        CacheOperation.Get => _getSpanName,
        CacheOperation.Set => _setSpanName,
        CacheOperation.Refresh => _refreshSpanName,
        CacheOperation.Remove => _removeSpanName,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };
}
