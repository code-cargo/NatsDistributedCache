namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Maintenance operations for a NATS-backed distributed cache that fall outside the
/// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> surface.
/// </summary>
/// <remarks>
/// Resolve this from dependency injection to reach the same cache instance registered by
/// <see cref="NatsDistributedCacheExtensions.AddNatsDistributedCache"/>.
/// </remarks>
public interface INatsCacheMaintenance
{
    /// <summary>
    /// Purges every cache entry whose key begins with <paramref name="prefix"/>, treated as a sub-prefix
    /// beneath the configured <see cref="NatsCacheOptions.CacheKeyPrefix"/> (entries stored as
    /// <c>{prefix}.{key}</c> are matched). This is a bulk, irreversible maintenance operation intended for
    /// scenarios such as evicting every entry belonging to a single tenant.
    /// </summary>
    /// <param name="prefix">
    /// The key sub-prefix to purge, relative to the configured cache key prefix. Must be non-empty and not
    /// consist solely of whitespace or <c>'.'</c> characters, so a scoped purge cannot collapse into a purge
    /// of the entire prefix space (or, when no <see cref="NatsCacheOptions.CacheKeyPrefix"/> is configured,
    /// the whole bucket).
    /// </param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The number of keys that were purged.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="prefix"/> is null, empty, whitespace, or consists solely of <c>'.'</c> characters.
    /// </exception>
    Task<long> PurgeByPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
