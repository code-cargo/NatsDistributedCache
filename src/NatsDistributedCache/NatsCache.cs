using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Cache entry for storing in NATS Key-Value Store
/// </summary>
public class CacheEntry
{
    public DateTimeOffset? AbsoluteExpiration { get; set; }

    public long? SlidingExpirationTicks { get; set; }

    public byte[]? Data { get; set; }
}

/// <summary>
/// Distributed cache implementation using NATS Key-Value Store.
/// </summary>
public partial class NatsCache : IBufferDistributedCache
{
    // JetStream "stream not found" error code (JSStreamNotFoundErr), returned by GetStoreAsync when the
    // KV bucket does not exist.
    private const int StreamNotFoundErrCode = 10059;

    // Compact binary serializer for the CacheEntry envelope (replaces the previous JSON+base64 format).
    private static readonly CacheEntryBinarySerializer CacheEntrySerializer = CacheEntryBinarySerializer.Default;

    // Non-zero LimitMarkerTTL enables per-key TTL (NATS 2.11+); the actual per-key expiry is set per Put.
    private static readonly TimeSpan DefaultLimitMarkerTtl = TimeSpan.FromSeconds(1);

    private readonly string _bucketName;
    private readonly bool _createBucketIfNotExists;
    private readonly Func<NatsKVConfig, NatsKVConfig>? _configureBucket;
    private readonly INatsCacheKeyEncoder _keyEncoder;
    private readonly string _keyPrefix;
    private readonly ILogger _logger;
    private readonly INatsConnection _natsConnection;
    private Lazy<Task<INatsKVStore>> _lazyKvStore;

    public NatsCache(
        IOptions<NatsCacheOptions> optionsAccessor,
        INatsConnection natsConnection,
        ILogger<NatsCache>? logger = null,
        INatsCacheKeyEncoder? keyEncoder = null)
    {
        var options = optionsAccessor.Value;
        _bucketName = !string.IsNullOrWhiteSpace(options.BucketName)
            ? options.BucketName
            : throw new ArgumentException(NatsCacheOptions.BucketNameRequiredMessage, nameof(NatsCacheOptions.BucketName));
        _keyPrefix = string.IsNullOrEmpty(options.CacheKeyPrefix)
            ? string.Empty
            : options.CacheKeyPrefix.TrimEnd('.');
        _createBucketIfNotExists = options.CreateBucketIfNotExists;
        _configureBucket = options.ConfigureBucket;
        _lazyKvStore = CreateLazyKvStore();
        _natsConnection = natsConnection;
        _logger = logger ?? NullLogger<NatsCache>.Instance;
        _keyEncoder = keyEncoder ?? new NatsCacheKeyEncoder();
    }

    // The clock used for all expiration calculations. Defaults to TimeProvider.System; the DI
    // registration overrides it with a TimeProvider resolved from the container when one is present
    // (for example, FakeTimeProvider in tests). Injected via init to keep the constructor unchanged.
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        var ttl = GetTtl(options);
        var entry = CreateCacheEntry(value, options);

        try
        {
            var kvStore = await GetKvStore().ConfigureAwait(false);

            // todo: remove cast after https://github.com/nats-io/nats.net/pull/852 is released
            await ((NatsKVStore)kvStore)
                .PutWithTtlAsync(GetEncodedKey(key), entry, ttl ?? TimeSpan.Zero, CacheEntrySerializer, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) =>
        SetAsync(key, value, options).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask SetAsync(
        string key,
        ReadOnlySequence<byte> value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        var array = value.IsSingleSegment ? value.First.ToArray() : value.ToArray();
        await SetAsync(key, array, options, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        try
        {
            await RemoveCoreAsync(key, null, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        try
        {
            await GetAndRefreshAsync(key, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        try
        {
            return await GetAndRefreshAsync(key, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryGet(string key, IBufferWriter<byte> destination) =>
        TryGetAsync(key, destination).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<bool> TryGetAsync(
        string key,
        IBufferWriter<byte> destination,
        CancellationToken token = default)
    {
        try
        {
            var result = await GetAndRefreshAsync(key, token).ConfigureAwait(false);
            if (result != null)
            {
                destination.Write(result);
                return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cooperative cancellation is not a cache failure; let it propagate rather than
            // masquerading as a cache miss.
            throw;
        }
        catch (Exception ex)
        {
            // A read failure (e.g. NATS connectivity or a corrupt entry) is swallowed to honor the
            // IBufferDistributedCache contract (return false), but logged at warning so it stays
            // visible in production and is distinguishable from a normal cache miss.
            LogSwallowedException(ex);
        }

        return false;
    }

    internal TimeSpan? GetTtl(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration.HasValue && options.AbsoluteExpiration.Value <= TimeProvider.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
                options.AbsoluteExpiration.Value,
                "The absolute expiration value must be in the future.");
        }

        if (options.AbsoluteExpirationRelativeToNow.HasValue &&
            options.AbsoluteExpirationRelativeToNow.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
                options.AbsoluteExpirationRelativeToNow.Value,
                "The relative expiration value must be positive.");
        }

        if (options.SlidingExpiration.HasValue && options.SlidingExpiration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.SlidingExpiration),
                options.SlidingExpiration.Value,
                "The sliding expiration value must be positive.");
        }

        var absoluteExpiration = ResolveAbsoluteExpiration(options);
        if (!absoluteExpiration.HasValue)
        {
            return options.SlidingExpiration;
        }

        var ttl = absoluteExpiration.Value - TimeProvider.GetUtcNow();
        if (ttl.TotalMilliseconds <= 0)
        {
            // Value is in the past, remove it
            return TimeSpan.Zero;
        }

        // If there's also a sliding expiration, use the minimum of the two
        return options.SlidingExpiration.HasValue
            ? TimeSpan.FromTicks(Math.Min(ttl.Ticks, options.SlidingExpiration.Value.Ticks))
            : ttl;
    }

    internal CacheEntry CreateCacheEntry(byte[] value, DistributedCacheEntryOptions options) =>
        new CacheEntry
        {
            Data = value,
            AbsoluteExpiration = ResolveAbsoluteExpiration(options),
            SlidingExpirationTicks = options.SlidingExpiration?.Ticks
        };

    // An entry is absolutely expired once the clock reaches its absolute expiration instant. The
    // boundary is inclusive (>=) to match GetTtl, which treats an absolute expiration at "now" as
    // already elapsed. Sliding expiration is enforced separately via the NATS entry TTL.
    internal bool IsAbsolutelyExpired(CacheEntry entry) =>
        entry.AbsoluteExpiration.HasValue && TimeProvider.GetUtcNow() >= entry.AbsoluteExpiration.Value;

    // Builds the NatsKVConfig used when CreateBucketIfNotExists is enabled. Pure and synchronous (does not
    // touch the NATS connection), so it is unit-testable without a server. Cache-appropriate defaults are
    // applied first, then the user hook (a record `with` transform) may override them. The bucket name is
    // re-asserted afterward so the hook cannot retarget creation to a bucket other than the one GetKvStore
    // reads from.
    internal NatsKVConfig BuildBucketConfig()
    {
        var config = new NatsKVConfig(_bucketName)
        {
            History = 1, // required for well-defined per-key TTL behavior
            LimitMarkerTTL = DefaultLimitMarkerTtl, // non-zero => enables per-key TTL (NATS 2.11+)
        };

        if (_configureBucket != null)
        {
            config = _configureBucket(config)
                     ?? throw new InvalidOperationException(
                         $"{nameof(NatsCacheOptions)}.{nameof(NatsCacheOptions.ConfigureBucket)} must not return null.");
            if (config.Bucket != _bucketName)
            {
                config = config with { Bucket = _bucketName };
            }
        }

        return config;
    }

    // Resolves the effective absolute expiration instant: a relative expiration (offset from the
    // current clock) takes precedence over an explicit absolute expiration when both are set.
    private DateTimeOffset? ResolveAbsoluteExpiration(DistributedCacheEntryOptions options) =>
        options.AbsoluteExpirationRelativeToNow.HasValue
            ? TimeProvider.GetUtcNow().Add(options.AbsoluteExpirationRelativeToNow.Value)
            : options.AbsoluteExpiration;

    private string GetEncodedKey(string key) =>
        string.IsNullOrEmpty(_keyPrefix)
            ? _keyEncoder.Encode(key)
            : _keyEncoder.Encode($"{_keyPrefix}.{key}");

    // Returns the KV store for the configured bucket, creating it only if it does not already exist. An
    // existing (operator-managed) bucket is used as-is and never modified — matching the
    // CreateBucketIfNotExists contract — so CreateStoreAsync is used rather than CreateOrUpdateStoreAsync.
    // GetStoreAsync is attempted first (rather than listing every bucket) so this stays O(1) and needs no
    // stream-list permission; only a genuine "stream not found" triggers creation, and any other error
    // (connectivity, auth) propagates unchanged. A create that loses a race with a concurrent creator
    // surfaces as a failure and is retried via the reset-on-failure path in CreateLazyKvStore.
    private async Task<INatsKVStore> GetOrCreateStoreAsync(INatsKVContext kv)
    {
        try
        {
            return await kv.GetStoreAsync(_bucketName).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error is { ErrCode: StreamNotFoundErrCode })
        {
            // Null-safe pattern: a null Error simply doesn't match, so the filter is false rather than throwing.
            return await kv.CreateStoreAsync(BuildBucketConfig()).ConfigureAwait(false);
        }
    }

    private Lazy<Task<INatsKVStore>> CreateLazyKvStore() =>
        new(async () =>
        {
            try
            {
                var kv = _natsConnection.CreateKeyValueStoreContext();
                var store = _createBucketIfNotExists
                    ? await GetOrCreateStoreAsync(kv).ConfigureAwait(false)
                    : await kv.GetStoreAsync(_bucketName).ConfigureAwait(false);
                LogConnected(_bucketName);
                return store;
            }
            catch (Exception)
            {
                // Reset the lazy initializer on failure so the next attempt retries. The exception
                // propagates to the calling operation, which logs it once at the appropriate level.
                _lazyKvStore = CreateLazyKvStore();
                throw;
            }
        });

    private Task<INatsKVStore> GetKvStore() => _lazyKvStore.Value;

    private async Task<byte[]?> GetAndRefreshAsync(string key, CancellationToken token)
    {
        var encodedKey = GetEncodedKey(key);
        var kvStore = await GetKvStore().ConfigureAwait(false);
        try
        {
            var natsResult = await kvStore
                .TryGetEntryAsync(encodedKey, serializer: CacheEntrySerializer, cancellationToken: token)
                .ConfigureAwait(false);
            if (!natsResult.Success)
            {
                return null;
            }

            var kvEntry = natsResult.Value;
            if (kvEntry.Value == null)
            {
                return null;
            }

            // Check absolute expiration
            if (IsAbsolutelyExpired(kvEntry.Value))
            {
                // NatsKVWrongLastRevisionException is caught below
                var natsDeleteOpts = new NatsKVDeleteOpts { Revision = kvEntry.Revision };
                await RemoveCoreAsync(key, natsDeleteOpts, token).ConfigureAwait(false);
                return null;
            }

            await UpdateEntryExpirationAsync(kvEntry).ConfigureAwait(false);
            return kvEntry.Value.Data;
        }
        catch (NatsKVWrongLastRevisionException)
        {
            // Someone else updated it; that's fine, we'll get the latest version next time
            return null;
        }

        // Local Functions
        async Task UpdateEntryExpirationAsync(NatsKVEntry<CacheEntry> kvEntry)
        {
            if (kvEntry.Value?.SlidingExpirationTicks == null)
            {
                return;
            }

            // If we have a sliding expiration, use it as the TTL
            var ttl = TimeSpan.FromTicks(kvEntry.Value.SlidingExpirationTicks.Value);

            // If we also have an absolute expiration, make sure we don't exceed it
            if (kvEntry.Value.AbsoluteExpiration != null)
            {
                var remainingTime = kvEntry.Value.AbsoluteExpiration.Value - TimeProvider.GetUtcNow();

                // Use the minimum of sliding window or remaining absolute time
                if (remainingTime > TimeSpan.Zero && remainingTime < ttl)
                {
                    ttl = remainingTime;
                }
            }

            if (ttl > TimeSpan.Zero)
            {
                // Use optimistic concurrency control with the last revision
                // todo: remove cast after https://github.com/nats-io/nats.net/pull/852 is released
                await kvStore.UpdateWithTtlAsync(
                    encodedKey,
                    kvEntry.Value,
                    kvEntry.Revision,
                    ttl,
                    serializer: CacheEntrySerializer,
                    cancellationToken: token).ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveCoreAsync(
        string key,
        NatsKVDeleteOpts? natsKvDeleteOpts = null,
        CancellationToken token = default)
    {
        var kvStore = await GetKvStore().ConfigureAwait(false);
        await kvStore.DeleteAsync(GetEncodedKey(key), natsKvDeleteOpts, cancellationToken: token)
            .ConfigureAwait(false);
    }
}
