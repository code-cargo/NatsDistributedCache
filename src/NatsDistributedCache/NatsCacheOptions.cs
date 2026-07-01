using Microsoft.Extensions.Options;

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

        NatsCacheOptions IOptions<NatsCacheOptions>.Value => this;
    }
}
