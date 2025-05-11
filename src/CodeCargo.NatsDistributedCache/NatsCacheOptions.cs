using Microsoft.Extensions.Options;

namespace CodeCargo.NatsDistributedCache
{
    /// <summary>
    /// Configuration options for <see cref="NatsCache"/>.
    /// </summary>
    public class NatsCacheOptions : IOptions<NatsCacheOptions>
    {
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
        /// If set, attempt to retrieve the INatsConnection as a keyed service from the service provider.
        /// </summary>
        public string? ConnectionServiceKey { get; set; }

        /// <summary>
        /// When true (the default) register a <see cref="NatsHybridCacheSerializerFactory"/> as an
        /// IHybridCacheSerializerFactory that uses the NATS Connection's Serializer Registry.
        /// </summary>
        public bool RegisterHybridCacheSerializerFactory { get; set; } = true;

        NatsCacheOptions IOptions<NatsCacheOptions>.Value => this;
    }
}
