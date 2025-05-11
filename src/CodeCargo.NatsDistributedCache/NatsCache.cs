using System.Buffers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache;

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
/// Distributed cache implementation using NATS Key-Value Store.
/// </summary>
public partial class NatsCache : IBufferDistributedCache, IDisposable
{
    // Static JSON serializer for CacheEntry
    private static readonly NatsJsonContextSerializer<CacheEntry> CacheEntrySerializer =
        new(CacheEntryJsonContext.Default);

    private readonly ILogger _logger;
    private readonly NatsCacheOptions _options;
    private readonly string _keyPrefix;
    private readonly INatsConnection _natsConnection;
    private readonly SemaphoreSlim _connectionLock = new(initialCount: 1, maxCount: 1);
    private NatsKVStore? _kvStore;
    private bool _disposed;

    public NatsCache(
        IOptions<NatsCacheOptions> optionsAccessor,
        ILogger<NatsCache> logger,
        INatsConnection natsConnection)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(natsConnection);

        _options = optionsAccessor.Value;
        _logger = logger;
        _natsConnection = natsConnection;
        _keyPrefix = string.IsNullOrEmpty(_options.CacheKeyPrefix) ? string.Empty : _options.CacheKeyPrefix.TrimEnd('.');
    }

    public NatsCache(IOptions<NatsCacheOptions> optionsAccessor, INatsConnection natsConnection)
        : this(optionsAccessor, NullLogger<NatsCache>.Instance, natsConnection)
    {
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();

        var ttl = GetTtl(options);
        var entry = CreateCacheEntry(value, options);
        var kvStore = await GetKvStore().ConfigureAwait(false);

        try
        {
            await kvStore.PutAsync(GetPrefixedKey(key), entry, ttl ?? TimeSpan.Zero, CacheEntrySerializer, token)
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();

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
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();

        await GetAndRefreshAsync(key, getData: false, retry: true, token: token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();

        return await GetAndRefreshAsync(key, getData: true, retry: true, token: token).ConfigureAwait(false);
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        token.ThrowIfCancellationRequested();

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

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // This is the method used by hybrid caching to determine if it should use the distributed instance
    internal virtual bool IsHybridCacheActive() => false;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // Dispose managed state (managed objects)
            _connectionLock.Dispose();
            _kvStore = null; // Set to null to ensure we don't use it after dispose
        }

        // Free unmanaged resources (unmanaged objects) and override finalizer
        _disposed = true;
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

    private string GetPrefixedKey(string key) => string.IsNullOrEmpty(_keyPrefix)
        ? key
        : _keyPrefix + "." + key;

    private async Task<NatsKVStore> GetKvStore()
    {
        if (_kvStore != null && !_disposed)
        {
            return _kvStore;
        }

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_kvStore == null || _disposed)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (string.IsNullOrEmpty(_options.BucketName))
                {
                    throw new InvalidOperationException("BucketName is required and cannot be null or empty.");
                }

                var jsContext = _natsConnection.CreateJetStreamContext();
                var kvContext = new NatsKVContext(jsContext);
                _kvStore = (NatsKVStore)await kvContext.GetStoreAsync(_options.BucketName).ConfigureAwait(false);
                if (_kvStore == null)
                {
                    throw new InvalidOperationException("Failed to create NATS KV store");
                }

                LogConnected();
            }
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }

        return _kvStore;
    }

    private async Task<byte[]?> GetAndRefreshAsync(
        string key,
        bool getData,
        bool retry,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        var kvStore = await GetKvStore().ConfigureAwait(false);
        var prefixedKey = GetPrefixedKey(key);
        try
        {
            var natsResult = await kvStore
                .TryGetEntryAsync(prefixedKey, serializer: CacheEntrySerializer, cancellationToken: token)
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

            await UpdateEntryExpirationAsync(kvStore, prefixedKey, kvEntry, token).ConfigureAwait(false);
            return getData ? kvEntry.Value.Data : null;
        }
        catch (NatsKVWrongLastRevisionException ex)
        {
            // Optimistic concurrency control failed, someone else updated it
            LogUpdateFailed(key);
            if (retry)
            {
                return await GetAndRefreshAsync(key, getData, retry: false, token).ConfigureAwait(false);
            }

            LogException(ex);
            return null;
        }
        catch (Exception ex)
        {
            LogException(ex);
            throw;
        }
    }

    private async Task UpdateEntryExpirationAsync(
        NatsKVStore kvStore,
        string key,
        NatsKVEntry<CacheEntry> kvEntry,
        CancellationToken token)
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
            try
            {
                await kvStore.UpdateAsync(
                    key,
                    kvEntry.Value,
                    kvEntry.Revision,
                    ttl,
                    serializer: CacheEntrySerializer,
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (NatsKVWrongLastRevisionException)
            {
                // Someone else updated it; that's fine, we'll get the latest version next time
                LogUpdateFailed(key.Replace(GetPrefixedKey(string.Empty), string.Empty));
            }
        }
    }

    /// <summary>
    /// Removes the value with the given key.
    /// </summary>
    /// <param name="key">A string identifying the requested value.</param>
    /// <param name="natsKvDeleteOpts">Nats KV delete options</param>
    /// <param name="token">Optional. The <see cref="CancellationToken"/> used to propagate notifications that the operation should be canceled.</param>
    /// <returns>The <see cref="Task"/> that represents the asynchronous operation.</returns>
    private async Task RemoveAsync(
        string key,
        NatsKVDeleteOpts? natsKvDeleteOpts = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();

        var kvStore = await GetKvStore().ConfigureAwait(false);
        await kvStore.DeleteAsync(GetPrefixedKey(key), natsKvDeleteOpts, cancellationToken: token).ConfigureAwait(false);
    }
}

[JsonSerializable(typeof(CacheEntry))]
internal partial class CacheEntryJsonContext : JsonSerializerContext
{
}
