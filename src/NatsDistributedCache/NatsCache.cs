using System.Buffers;
using System.Diagnostics.Metrics;
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
    private readonly Func<NatsKVConfig, NatsKVConfig>? _configureBucketOnCreate;
    private readonly INatsCacheKeyEncoder _keyEncoder;
    private readonly string _keyPrefix;
    private readonly ILogger _logger;
    private readonly INatsConnection _natsConnection;
    private readonly Lazy<NatsCacheTelemetry> _telemetry;
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
        _configureBucketOnCreate = options.ConfigureBucketOnCreate;
        _lazyKvStore = CreateLazyKvStore();
        _natsConnection = natsConnection;
        _logger = logger ?? NullLogger<NatsCache>.Instance;
        _keyEncoder = keyEncoder ?? new NatsCacheKeyEncoder();

        // Built lazily because the factory reads MeterFactory, an init-only property that is assigned after
        // this constructor body runs. Lazy (rather than a ??= on a volatile field) so a startup race cannot
        // create a second set of Instruments that get published to listeners but never recorded to.
        var recordCacheKeys = options.Telemetry.RecordCacheKeys;
        _telemetry = new Lazy<NatsCacheTelemetry>(
            () => new NatsCacheTelemetry(MeterFactory, _bucketName, recordCacheKeys),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // The clock used for all expiration calculations. Defaults to TimeProvider.System; the DI
    // registration overrides it with a TimeProvider resolved from the container when one is present
    // (for example, FakeTimeProvider in tests). Injected via init to keep the constructor unchanged.
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    // The IMeterFactory used to create the cache's Meter, resolved from DI when present. Injected via init
    // (like TimeProvider above) to keep the public constructor unchanged. When absent — direct construction,
    // or a container without AddMetrics() — telemetry falls back to a process-wide static Meter of the same
    // name, so subscribers see identical instruments either way.
    internal IMeterFactory? MeterFactory { get; init; }

    private NatsCacheTelemetry Telemetry => _telemetry.Value;

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
        // GetTtl throws ArgumentOutOfRangeException for caller-supplied bad expirations. The scope opens
        // after it deliberately: that is a caller bug that never reaches NATS, so counting it would fire the
        // error-rate alert on an operation that was never attempted and inject a ~0s latency sample.
        var ttl = GetTtl(options);
        var entry = CreateCacheEntry(value, options);

        // The single terminal implementation for all four Set entry points. Set(byte[]), Set(ReadOnlySequence),
        // and SetAsync(ReadOnlySequence) delegate here and are deliberately left uninstrumented, so the
        // three-deep Set(ReadOnlySequence) chain still records exactly one measurement.
        var scope = NatsCacheOperationScope.Start(Telemetry, TimeProvider, CacheOperation.Set, key, token);
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
            scope.SetError(ex);
            LogException(ex);
            throw;
        }
        finally
        {
            scope.Complete();
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
        // Instrumented here rather than in RemoveCoreAsync, which is also called from GetAndRefreshAsync's
        // absolute-expiry eviction path. Instrumenting the core would emit a phantom operation=remove for
        // every expired read — inflating remove rate and nesting a bogus remove span inside a get. The
        // eviction's latency correctly lands inside the enclosing get instead, because the caller waited.
        var scope = NatsCacheOperationScope.Start(Telemetry, TimeProvider, CacheOperation.Remove, key, token);
        try
        {
            await RemoveCoreAsync(key, null, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.SetError(ex);
            LogException(ex);
            throw;
        }
        finally
        {
            scope.Complete();
        }
    }

    /// <inheritdoc />
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        try
        {
            await GetAndRefreshAsync(key, CacheOperation.Refresh, token).ConfigureAwait(false);
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
            return await GetAndRefreshAsync(key, CacheOperation.Get, token).ConfigureAwait(false);
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
            // Reports as operation=get, not a distinct operation: the IBufferWriter overload is a zero-copy
            // detail, and merging it keeps hit ratio computed over all read paths — which matters because
            // HybridCache drives its L2 reads exclusively through this method.
            var result = await GetAndRefreshAsync(key, CacheOperation.Get, token).ConfigureAwait(false);
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
        // Maximum-value sentinels mean "never expire": normalize them to no expiration so they are not
        // rejected as out-of-range below. Callers commonly use DateTimeOffset.MaxValue / TimeSpan.MaxValue
        // as a "cache forever" idiom, which is equivalent to omitting expiration entirely.
        var absoluteExpiration = EffectiveAbsoluteExpiration(options);
        var relativeToNow = EffectiveRelativeToNow(options);
        var slidingExpiration = EffectiveSlidingExpiration(options);

        if (absoluteExpiration.HasValue && absoluteExpiration.Value <= TimeProvider.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
                absoluteExpiration.Value,
                "The absolute expiration value must be in the future.");
        }

        if (relativeToNow.HasValue && relativeToNow.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
                relativeToNow.Value,
                "The relative expiration value must be positive.");
        }

        if (slidingExpiration.HasValue && slidingExpiration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.SlidingExpiration),
                slidingExpiration.Value,
                "The sliding expiration value must be positive.");
        }

        // Reject sliding windows the serializer/TTL encoding could not round-trip. The read path fails
        // closed above MaxTtlTicks, so accepting a larger value here would silently store an entry that
        // later reads back as an undeserializable cache miss.
        if (slidingExpiration.HasValue &&
            slidingExpiration.Value.Ticks > CacheEntryBinarySerializer.MaxTtlTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.SlidingExpiration),
                slidingExpiration.Value,
                $"The sliding expiration value is too large. The maximum is {CacheEntryBinarySerializer.MaxTtl}.");
        }

        var resolvedAbsolute = ResolveAbsoluteExpiration(options);
        if (!resolvedAbsolute.HasValue)
        {
            return slidingExpiration;
        }

        var ttl = resolvedAbsolute.Value - TimeProvider.GetUtcNow();
        if (ttl.TotalMilliseconds <= 0)
        {
            // Value is in the past, remove it
            return TimeSpan.Zero;
        }

        // If there's also a sliding expiration, use the minimum of the two. Sliding is bounded to
        // MaxTtlTicks above, so the minimum is always within the encodable range.
        if (slidingExpiration.HasValue)
        {
            return TimeSpan.FromTicks(Math.Min(ttl.Ticks, slidingExpiration.Value.Ticks));
        }

        // Absolute-only: the TTL spans the full window to the absolute instant. Reject windows the NATS
        // TTL encoding cannot represent (see CacheEntryBinarySerializer.MaxTtlTicks) rather than emitting
        // an overflowed header.
        if (ttl.Ticks > CacheEntryBinarySerializer.MaxTtlTicks)
        {
            if (relativeToNow.HasValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
                    relativeToNow.Value,
                    $"The relative expiration value is too large. The maximum is {CacheEntryBinarySerializer.MaxTtl}.");
            }

            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
                absoluteExpiration!.Value,
                $"The absolute expiration is too far in the future. The maximum window is {CacheEntryBinarySerializer.MaxTtl}.");
        }

        return ttl;
    }

    internal CacheEntry CreateCacheEntry(byte[] value, DistributedCacheEntryOptions options) =>
        new CacheEntry
        {
            Data = value,
            AbsoluteExpiration = ResolveAbsoluteExpiration(options),
            SlidingExpirationTicks = EffectiveSlidingExpiration(options)?.Ticks
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

        if (_configureBucketOnCreate != null)
        {
            config = _configureBucketOnCreate(config)
                     ?? throw new InvalidOperationException(
                         $"{nameof(NatsCacheOptions)}.{nameof(NatsCacheOptions.ConfigureBucketOnCreate)} must not return null.");
            if (config.Bucket != _bucketName)
            {
                config = config with { Bucket = _bucketName };
            }
        }

        return config;
    }

    // "Never expire" sentinels: a DateTimeOffset.MaxValue absolute instant or a TimeSpan.MaxValue window
    // is normalized to no expiration, so it is not rejected as out-of-range and the entry lives forever.
    private static DateTimeOffset? EffectiveAbsoluteExpiration(DistributedCacheEntryOptions options) =>
        options.AbsoluteExpiration == DateTimeOffset.MaxValue ? null : options.AbsoluteExpiration;

    private static TimeSpan? EffectiveRelativeToNow(DistributedCacheEntryOptions options) =>
        options.AbsoluteExpirationRelativeToNow == TimeSpan.MaxValue ? null : options.AbsoluteExpirationRelativeToNow;

    private static TimeSpan? EffectiveSlidingExpiration(DistributedCacheEntryOptions options) =>
        options.SlidingExpiration == TimeSpan.MaxValue ? null : options.SlidingExpiration;

    // Resolves the effective absolute expiration instant: a relative expiration (offset from the
    // current clock) takes precedence over an explicit absolute expiration when both are set.
    // Maximum-value sentinels are treated as "no expiration" (see the Effective* helpers).
    private DateTimeOffset? ResolveAbsoluteExpiration(DistributedCacheEntryOptions options)
    {
        var relativeToNow = EffectiveRelativeToNow(options);
        return relativeToNow.HasValue
            ? TimeProvider.GetUtcNow().Add(relativeToNow.Value)
            : EffectiveAbsoluteExpiration(options);
    }

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

    // The shared read core for Get, TryGet, and Refresh (and their sync overloads). Instrumented here
    // rather than in the three public methods so that TryGetAsync's swallowed failures are still recorded
    // as errors — the scope closes before the exception reaches TryGetAsync's catch — and so no read path
    // can be counted twice.
    private async Task<byte[]?> GetAndRefreshAsync(string key, CacheOperation operation, CancellationToken token)
    {
        var scope = NatsCacheOperationScope.Start(Telemetry, TimeProvider, operation, key, token);
        try
        {
            // GetKvStore is inside the scope so first-use connect and bucket-creation failures are recorded
            // as errors too. This does not change which exceptions propagate.
            var encodedKey = GetEncodedKey(key);
            var kvStore = await GetKvStore().ConfigureAwait(false);
            try
            {
                var natsResult = await kvStore
                    .TryGetEntryAsync(encodedKey, serializer: CacheEntrySerializer, cancellationToken: token)
                    .ConfigureAwait(false);
                if (!natsResult.Success)
                {
                    scope.SetMiss(CacheMissReason.NotFound);
                    return null;
                }

                var kvEntry = natsResult.Value;
                if (kvEntry.Value == null)
                {
                    // Present entry whose bytes we cannot deserialize: a legacy JSON envelope from a
                    // pre-binary release, or genuine corruption. Intended behavior is to treat it as a
                    // cache miss and leave the entry in place. It self-heals when the key is next written
                    // (Set overwrites unconditionally), and any TTL'd entry is reaped by NATS. We
                    // deliberately do not evict it (an older node must not delete entries written in a
                    // newer format during a rolling deploy) nor throw (a cache should degrade to a miss,
                    // not fail the caller's operation). Logged at Debug to aid diagnosis without flooding
                    // logs during a JSON->binary migration, when every legacy key transiently lands here.
                    LogUndeserializableEntry(key);
                    scope.SetMiss(CacheMissReason.Undeserializable);
                    return null;
                }

                // Check absolute expiration
                if (IsAbsolutelyExpired(kvEntry.Value))
                {
                    // NatsKVWrongLastRevisionException is caught below
                    var natsDeleteOpts = new NatsKVDeleteOpts { Revision = kvEntry.Revision };
                    await RemoveCoreAsync(key, natsDeleteOpts, token).ConfigureAwait(false);
                    scope.SetMiss(CacheMissReason.Expired);
                    return null;
                }

                await UpdateEntryExpirationAsync(kvEntry).ConfigureAwait(false);
                scope.SetHit();
                return kvEntry.Value.Data;
            }
            catch (NatsKVWrongLastRevisionException)
            {
                // Someone else updated it; that's fine, we'll get the latest version next time
                scope.SetMiss(CacheMissReason.RevisionConflict);
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
        catch (Exception ex)
        {
            scope.SetError(ex);
            throw;
        }
        finally
        {
            scope.Complete();
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
