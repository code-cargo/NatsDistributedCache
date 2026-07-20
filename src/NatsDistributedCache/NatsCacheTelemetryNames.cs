namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Well-known meter, activity source, and instrument names emitted by <see cref="NatsCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// Telemetry is emitted through <c>System.Diagnostics.Metrics</c> and <c>System.Diagnostics.ActivitySource</c>
/// only; this package takes no dependency on OpenTelemetry. No measurement is recorded, no duration is timed,
/// and no per-operation allocation occurs until a listener subscribes, so registering these names is the
/// opt-in:
/// </para>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(metrics =&gt; metrics.AddMeter(NatsCacheTelemetryNames.MeterName))
///     .WithTracing(tracing =&gt; tracing.AddSource(NatsCacheTelemetryNames.ActivitySourceName));
/// </code>
/// <para>
/// These are compile-time constants, so referencing them does not initialize the meter.
/// </para>
/// </remarks>
public static class NatsCacheTelemetryNames
{
    /// <summary>
    /// Name of the <c>Meter</c> that publishes the cache instruments. Pass to <c>AddMeter</c>.
    /// </summary>
    public const string MeterName = "CodeCargo.Nats.DistributedCache";

    /// <summary>
    /// Name of the <c>ActivitySource</c> that publishes cache spans. Pass to <c>AddSource</c>.
    /// </summary>
    public const string ActivitySourceName = "CodeCargo.Nats.DistributedCache";

    /// <summary>
    /// Histogram of cache operation durations, in seconds. Its count also yields operation rate, hit ratio,
    /// and error rate via the <c>nats.cache.result</c> tag.
    /// </summary>
    public const string OperationDurationInstrumentName = "nats.cache.operation.duration";

    /// <summary>
    /// Counter of cache read misses, broken down by <c>nats.cache.miss.reason</c>.
    /// </summary>
    public const string MissesInstrumentName = "nats.cache.misses";
}
