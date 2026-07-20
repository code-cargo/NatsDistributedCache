namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Telemetry configuration for <see cref="NatsCache"/>.
/// </summary>
/// <remarks>
/// Telemetry is inert until a listener subscribes to the meter or activity source named by
/// <see cref="NatsCacheTelemetryNames"/>; these options only tune what is emitted once something is
/// listening, they are not the opt-in switch.
/// </remarks>
public class NatsCacheTelemetryOptions
{
    /// <summary>
    /// When <see langword="true"/>, the unencoded cache key is added to cache spans as the
    /// <c>nats.cache.key</c> attribute. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Cache keys frequently embed user, tenant, or session identifiers, so they are omitted by default.
    /// This setting affects spans only — the key is never applied to metrics under any setting, because
    /// it is an unbounded dimension that would break metric cardinality.
    /// </remarks>
    public bool RecordCacheKeys { get; set; }
}
