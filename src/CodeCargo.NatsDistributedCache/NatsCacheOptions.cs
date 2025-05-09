// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        /// The NATS instance name. Allows partitioning a single backend cache for use with multiple apps/services.
        /// If set, the cache keys are prefixed with this value.
        /// </summary>
        public string? InstanceName { get; set; }

        NatsCacheOptions IOptions<NatsCacheOptions>.Value => this;
    }
}
