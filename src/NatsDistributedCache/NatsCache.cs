using System.Buffers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Cache entry for storing in NATS Key-Value Store
/// </summary>
public class CacheEntry
{
    [JsonPropertyName("absexp")]
    public DateTimeOffset? AbsoluteExpiration { get; set; }

    [JsonPropertyName("sldexp")]
    public long? SlidingExpirationTicks { get; set; }

    [JsonPropertyName("data")]
    public byte[]? Data { get; set; }
}

/// <summary>
/// JsonSerializerContext for CacheEntry
/// </summary>
[JsonSerializable(typeof(CacheEntry))]
public partial class CacheEntryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Distributed cache implementation using NATS Key-Value Store.
/// </summary>
public partial class NatsCache : IBufferDistributedCache
{
    // Static JSON serializer for CacheEntry
    private static readonly NatsJsonContextSerializer<CacheEntry> CacheEntrySerializer =
        new(CacheEntryJsonContext.Default);

    private readonly string _bucketName;
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
            : throw new NullReferenceException("BucketName must be set");
        _keyPrefix = string.IsNullOrEmpty(options.CacheKeyPrefix)
            ? string.Empty
            : options.CacheKeyPrefix.TrimEnd('.');
        _lazyKvStore = CreateLazyKvStore();
        _natsConnection = natsConnection;
        _logger = logger ?? NullLogger<NatsCache>.Instance;
        _keyEncoder = keyEncoder ?? new NatsCacheKeyEncoder();
    }

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
        var kvStore = await GetKvStore().ConfigureAwait(false);

        try
        {
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
    public void Remove(string key) => RemoveAsync(key, null).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken token = default) =>
        await RemoveAsync(key, null, token).ConfigureAwait(false);

    /// <inheritdoc />
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) =>
        GetAndRefreshAsync(key, token: token);

    /// <inheritdoc />
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        GetAndRefreshAsync(key, token: token);

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
            var result = await GetAsync(key, token).ConfigureAwait(false);
            if (result != null)
            {
                destination.Write(result);
                return true;
            }
        }
        catch
        {
            // Ignore failures here; they will surface later
        }

        return false;
    }

    private static TimeSpan? GetTtl(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration.HasValue && options.AbsoluteExpiration.Value <= DateTimeOffset.Now)
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

        var absoluteExpiration = options.AbsoluteExpiration;
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
        {
            absoluteExpiration = DateTimeOffset.Now.Add(options.AbsoluteExpirationRelativeToNow.Value);
        }

        if (!absoluteExpiration.HasValue)
        {
            return options.SlidingExpiration;
        }

        var ttl = absoluteExpiration.Value - DateTimeOffset.Now;
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

    private static CacheEntry CreateCacheEntry(byte[] value, DistributedCacheEntryOptions options)
    {
        var absoluteExpiration = options.AbsoluteExpiration;
        if (options.AbsoluteExpirationRelativeToNow.HasValue)
        {
            absoluteExpiration = DateTimeOffset.Now.Add(options.AbsoluteExpirationRelativeToNow.Value);
        }

        var cacheEntry = new CacheEntry
        {
            Data = value,
            AbsoluteExpiration = absoluteExpiration,
            SlidingExpirationTicks = options.SlidingExpiration?.Ticks
        };

        return cacheEntry;
    }

    private string GetEncodedKey(string key) =>
        string.IsNullOrEmpty(_keyPrefix)
            ? _keyEncoder.Encode(key)
            : _keyEncoder.Encode($"{_keyPrefix}.{key}");

    private Lazy<Task<INatsKVStore>> CreateLazyKvStore() =>
        new(async () =>
        {
            try
            {
                var kv = _natsConnection.CreateKeyValueStoreContext();
                var store = await kv.GetStoreAsync(_bucketName).ConfigureAwait(false);
                LogConnected(_bucketName);
                return store;
            }
            catch (Exception ex)
            {
                // Reset the lazy initializer on failure for next attempt
                _lazyKvStore = CreateLazyKvStore();

                LogException(ex);
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
            if (kvEntry.Value.AbsoluteExpiration != null && DateTimeOffset.Now > kvEntry.Value.AbsoluteExpiration)
            {
                // NatsKVWrongLastRevisionException is caught below
                var natsDeleteOpts = new NatsKVDeleteOpts { Revision = kvEntry.Revision };
                await RemoveAsync(key, natsDeleteOpts, token).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            LogException(ex);
            throw;
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
                var remainingTime = kvEntry.Value.AbsoluteExpiration.Value - DateTimeOffset.Now;

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

    private async Task RemoveAsync(
            string key,
            NatsKVDeleteOpts? natsKvDeleteOpts = null,
            CancellationToken token = default)
    {
        var kvStore = await GetKvStore().ConfigureAwait(false);
        await kvStore.DeleteAsync(GetEncodedKey(key), natsKvDeleteOpts, cancellationToken: token)
            .ConfigureAwait(false);
    }
}
