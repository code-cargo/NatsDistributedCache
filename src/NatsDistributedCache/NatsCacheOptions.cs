using Microsoft.Extensions.Options;
using NATS.Client.KeyValueStore;

namespace CodeCargo.Nats.DistributedCache
{
    /// <summary>
    /// Configuration options for <see cref="NatsCache"/>.
    /// </summary>
    public class NatsCacheOptions : IOptions<NatsCacheOptions>
    {
        // Shared by the startup options validator (AddNatsDistributedCache) and the NatsCache
        // constructor guard so both validation paths report an identical message.
        internal const string BucketNameRequiredMessage = "BucketName must be set";

        /// <summary>
        /// The NATS bucket name to use for the distributed cache.
        /// </summary>
        public string? BucketName { get; set; }

        /// <summary>
        /// If set, all cache keys are prefixed with this value followed by a period.
        /// Allows partitioning a single backend cache for use with multiple apps/services.
        /// </summary>
        public string? CacheKeyPrefix { get; set; }

        /// <summary>
        /// When <see langword="true"/>, the <see cref="BucketName"/> KV bucket is created the first time the
        /// cache is used, if it does not already exist. Defaults to <see langword="false"/>, in which case the
        /// bucket must be pre-created by the operator.
        /// </summary>
        /// <remarks>
        /// Only a missing bucket is created; an existing bucket is used as-is and never modified, so
        /// operator-managed settings are preserved. Creating a bucket requires JetStream stream-management
        /// permissions.
        /// </remarks>
        public bool CreateBucketIfNotExists { get; set; }

        /// <summary>
        /// Optional hook to customize the <see cref="NatsKVConfig"/> used when
        /// <see cref="CreateBucketIfNotExists"/> is enabled and a missing bucket is created (for example
        /// <c>Storage</c>, <c>NumberOfReplicas</c>, <c>MaxBytes</c>, or <c>MaxAge</c>). Ignored when
        /// <see cref="CreateBucketIfNotExists"/> is <see langword="false"/> or the bucket already exists.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="NatsKVConfig"/> is an immutable record, so the hook receives the pre-populated config
        /// and returns a modified copy using a <c>with</c> expression, for example
        /// <c>options.ConfigureBucketOnCreate = cfg =&gt; cfg with { Storage = NatsKVStorageType.Memory };</c>.
        /// </para>
        /// <para>
        /// The library pre-populates the config with cache-appropriate defaults — <c>History = 1</c> and a
        /// non-zero <c>LimitMarkerTTL</c>, both required for per-key TTL on NATS 2.11+ — before this hook runs,
        /// so the hook can override any property. The <c>Bucket</c> name is always forced back to
        /// <see cref="BucketName"/> afterward. Overriding <c>History</c> to a value other than 1, or clearing
        /// <c>LimitMarkerTTL</c>, disables reliable per-key TTL.
        /// </para>
        /// </remarks>
        public Func<NatsKVConfig, NatsKVConfig>? ConfigureBucketOnCreate { get; set; }

        /// <summary>
        /// Telemetry configuration. No metrics or traces are recorded until a listener subscribes to the
        /// meter or activity source named by <see cref="NatsCacheTelemetryNames"/>, so these options tune
        /// what is emitted rather than whether telemetry is enabled.
        /// </summary>
        /// <remarks>
        /// Get-only so it can never be null: configure it in place
        /// (<c>options.Telemetry.RecordCacheKeys = true</c>). Configuration binding populates get-only
        /// complex properties by binding into the existing instance, so <c>Bind</c> still works.
        /// </remarks>
        public NatsCacheTelemetryOptions Telemetry { get; } = new();

        NatsCacheOptions IOptions<NatsCacheOptions>.Value => this;
    }
}
