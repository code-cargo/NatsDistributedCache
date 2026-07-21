using System.Diagnostics;

namespace CodeCargo.Nats.DistributedCache;

// Measures one logical cache operation, emitting a duration histogram sample, a misses counter increment,
// and a client span.
//
// A plain (non-ref) mutable struct so it can live as a local across awaits: it is hoisted into the async
// state machine that is already heap-allocated for these methods, rather than allocating on its own.
//
// Completed via an explicit try/finally { scope.Complete(); } rather than IDisposable + using, because the
// struct is mutated by SetHit/SetMiss/SetError and `using` on a struct has by-value capture semantics that
// are easy to get subtly wrong.
internal struct NatsCacheOperationScope
{
    private NatsCacheTelemetry? _telemetry;
    private Activity? _activity;
    private TimeProvider? _timeProvider;
    private CancellationToken _token;
    private bool _durationEnabled;
    private long _startTimestamp;
    private string _operation;
    private string _result;
    private string? _missReason;
    private string? _errorType;

    // Opens a scope. When nothing is listening this is two volatile field reads and a zeroed struct: no
    // timestamp, no TagList, no Activity, no allocation. Taking the timestamp only when the histogram is
    // enabled is the load-bearing detail — Stopwatch.GetTimestamp() is a ~15-25ns clock_gettime call and
    // would otherwise dominate the disabled path.
    //
    // The caller's token is captured so SetError can tell cooperative cancellation apart from a failure
    // that merely surfaced as an OperationCanceledException (see SetError).
    internal static NatsCacheOperationScope Start(
        NatsCacheTelemetry telemetry,
        TimeProvider timeProvider,
        CacheOperation operation,
        string key,
        CancellationToken token)
    {
        var scope = default(NatsCacheOperationScope);

        // Gated on either instrument, not just the histogram: dropping the duration histogram via an OTel
        // View is a documented way to cut cost, and doing so must not silently take the misses counter
        // with it. The timestamp is still taken only for the histogram (see below).
        var durationEnabled = telemetry.OperationDuration.Enabled;
        var metricsEnabled = durationEnabled || telemetry.Misses.Enabled;
        var activity = NatsCacheTelemetry.ActivitySource.HasListeners()
            ? NatsCacheTelemetry.ActivitySource.StartActivity(
                telemetry.SpanName(operation),
                ActivityKind.Client)
            : null;

        if (!metricsEnabled && activity is null)
        {
            return scope;
        }

        scope._telemetry = telemetry;
        scope._timeProvider = timeProvider;
        scope._token = token;
        scope._operation = TelemetryTags.Name(operation);
        scope._result = TelemetryTags.ResultOk;
        scope._activity = activity;

        // A dedicated flag rather than testing the timestamp against zero: a TimeProvider is caller-supplied
        // (NatsCache resolves one from DI), and one whose clock origin is zero would make a legitimate
        // timestamp indistinguishable from "never taken", silently dropping every duration sample.
        scope._durationEnabled = durationEnabled;
        scope._startTimestamp = durationEnabled ? timeProvider.GetTimestamp() : 0L;

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(TelemetryTags.Operation, scope._operation);
            activity.SetTag(TelemetryTags.Bucket, telemetry.BucketName);

            // Span attribute only, and opt-in: cache keys routinely embed user or tenant identifiers. The
            // key is never applied to metrics under any setting — it is an unbounded dimension.
            if (telemetry.RecordCacheKeys)
            {
                activity.SetTag(TelemetryTags.Key, key);
            }
        }

        return scope;
    }

    internal void SetHit() => _result = TelemetryTags.ResultHit;

    internal void SetMiss(CacheMissReason reason)
    {
        _result = TelemetryTags.ResultMiss;
        _missReason = TelemetryTags.Name(reason);
    }

    internal void SetError(Exception exception)
    {
        // Only the caller's own cancellation is reported as `cancelled`; everything else is an error, even
        // when it surfaces as an OperationCanceledException. NATS request timeouts and internal token
        // cancellations arrive as TaskCanceledException with the caller's token unsignalled, and those are
        // genuine failures — TryGetAsync already draws this exact line with `when
        // (token.IsCancellationRequested)` before logging the rest at Warning. Classifying them as
        // `cancelled` would hide real outages from the error rate, because callers are told they can
        // exclude `cancelled` from alerting.
        if (exception is OperationCanceledException && _token.IsCancellationRequested)
        {
            _result = TelemetryTags.ResultCancelled;
            return;
        }

        _result = TelemetryTags.ResultError;
        _errorType = exception.GetType().FullName ?? exception.GetType().Name;
    }

    internal void Complete()
    {
        if (_telemetry is null)
        {
            return;
        }

        // Telemetry must never break the operation it measures. Complete() is always called from a finally
        // block, so an exception escaping here would replace the in-flight cache exception with a bogus one
        // — or fail an operation that actually succeeded. Meter and ActivitySource invoke listener callbacks
        // (exporters, third-party listeners) without guarding them, so a badly-behaved subscriber really can
        // throw into this frame. Each stage is isolated so one failing subscriber cannot suppress the other.
        try
        {
            RecordMetrics();
        }
        catch (Exception)
        {
            // Swallowed by design; see above. Our own faults here are caught by the telemetry test suite.
        }

        try
        {
            StopActivity();
        }
        catch (Exception)
        {
            // Swallowed by design; see above.
        }
    }

    private void RecordMetrics()
    {
        if (_durationEnabled)
        {
            var tags = default(TagList);
            tags.Add(TelemetryTags.Operation, _operation);
            tags.Add(TelemetryTags.Bucket, _telemetry!.BucketName);
            tags.Add(TelemetryTags.Result, _result);
            if (_errorType is not null)
            {
                tags.Add(TelemetryTags.ErrorType, _errorType);
            }

            // TagList holds 8 tags inline, so at 4 it never spills to an array, and every value is an
            // interned const string or the cache's own bucket name, so passing them as object? never boxes.
            _telemetry.OperationDuration.Record(_timeProvider!.GetElapsedTime(_startTimestamp).TotalSeconds, in tags);
        }

        // Recorded independently of the histogram above, so subscribing to only the misses counter (or
        // dropping the histogram with a View) still yields miss data. Add on a disabled instrument is a
        // cheap no-op, so no extra guard is needed.
        //
        // The reason dimension lives on this counter and never on the histogram: a 4-valued tag would
        // multiply the histogram's bucket arrays by 4 for a dimension nobody correlates with latency.
        if (_missReason is not null)
        {
            var missTags = default(TagList);
            missTags.Add(TelemetryTags.Operation, _operation);
            missTags.Add(TelemetryTags.Bucket, _telemetry!.BucketName);
            missTags.Add(TelemetryTags.MissReason, _missReason);
            _telemetry.Misses.Add(1, in missTags);
        }
    }

    private void StopActivity()
    {
        if (_activity is null)
        {
            return;
        }

        try
        {
            if (_activity.IsAllDataRequested)
            {
                _activity.SetTag(TelemetryTags.Result, _result);
                if (_missReason is not null)
                {
                    _activity.SetTag(TelemetryTags.MissReason, _missReason);
                }

                if (_errorType is not null)
                {
                    _activity.SetTag(TelemetryTags.ErrorType, _errorType);

                    // Error status only on a genuine failure. A miss is a normal outcome and stays Unset —
                    // marking misses as errors makes every cold cache look like an outage in Jaeger/Tempo.
                    // Ok is never set; that is the caller's call to make, not a library's.
                    _activity.SetStatus(ActivityStatusCode.Error, _errorType);
                }
            }
        }
        catch (Exception)
        {
            // Tagging failed; the activity must still be stopped below.
        }

        try
        {
            _activity.Dispose();
        }
        catch (Exception)
        {
            // Activity.Stop notifies ActivityStopped subscribers *before* restoring Activity.Current, so a
            // throwing subscriber leaves Current pointing at the activity we just stopped. Every later span
            // on this execution context would then be parented under a finished span, and the leak persists
            // for the lifetime of the async flow. Restore the parent ourselves.
            Activity.Current = _activity.Parent;
        }
    }
}
