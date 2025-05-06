// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache
{
    [JsonSerializable(typeof(CacheEntry))]
    internal partial class CacheEntryJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Cache entry for storing in NATS Key-Value Store
    /// </summary>
    public class CacheEntry
    {
        [JsonPropertyName("absexp")]
        public string? AbsoluteExpiration { get; set; }

        [JsonPropertyName("sldexp")]
        public string? SlidingExpiration { get; set; }

        [JsonPropertyName("data")]
        public byte[]? Data { get; set; }
    }

    /// <summary>
    /// Distributed cache implementation using NATS Key-Value Store.
    /// </summary>
    public partial class NatsCache : IBufferDistributedCache, IDisposable
    {
        private const string AbsoluteExpirationKey = "absexp";
        private const string SlidingExpirationKey = "sldexp";
        private const string DataKey = "data";

        // combined keys - same hash keys fetched constantly; avoid allocating an array each time
        private static readonly string[] GetHashFieldsNoData = new[] { AbsoluteExpirationKey, SlidingExpirationKey };

        private static readonly string[] GetHashFieldsWithData =
            new[] { AbsoluteExpirationKey, SlidingExpirationKey, DataKey };

        // Static JSON serializer for CacheEntry
        private static readonly NatsJsonContextSerializer<CacheEntry> _cacheEntrySerializer =
            new NatsJsonContextSerializer<CacheEntry>(CacheEntryJsonContext.Default);

        private readonly ILogger _logger;
        private readonly NatsCacheOptions _options;
        private readonly string _instanceName;
        private readonly INatsConnection _natsConnection;
        private NatsKVStore? _kvStore;
        private bool _disposed;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);

        public NatsCache(IOptions<NatsCacheOptions> optionsAccessor, ILogger<NatsCache> logger,
            INatsConnection natsConnection)
        {
            if (optionsAccessor == null)
            {
                throw new ArgumentNullException(nameof(optionsAccessor));
            }

            _options = optionsAccessor.Value;
            _logger = logger;
            _natsConnection = natsConnection ?? throw new ArgumentNullException(nameof(natsConnection));
            _instanceName = _options.InstanceName ?? string.Empty;

            // No need to connect immediately; will connect on-demand
        }

        public NatsCache(IOptions<NatsCacheOptions> optionsAccessor, INatsConnection natsConnection)
            : this(optionsAccessor, NullLogger<NatsCache>.Instance, natsConnection)
        {
        }

        // This is the method used by hybrid caching to determine if it should use the distributed instance
        internal virtual bool IsHybridCacheActive() => false;

        private string GetKeyPrefix(string key)
        {
            return string.IsNullOrEmpty(_instanceName)
                ? key
                : _instanceName + ":" + key;
        }

        /// <summary>
        /// Gets or sets a value with the given key.
        /// </summary>
        /// <param name="key">The key to get the value for.</param>
        /// <returns>The value for the given key, or null if not found.</returns>
        public byte[]? Get(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return GetAndRefresh(key, getData: true);
        }

        /// <summary>
        /// Asynchronously gets or sets a value with the given key.
        /// </summary>
        /// <param name="key">The key to get the value for.</param>
        /// <param name="token">Optional. A <see cref="CancellationToken" /> to cancel the operation.</param>
        /// <returns>The value for the given key, or null if not found.</returns>
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            token.ThrowIfCancellationRequested();

            return await GetAndRefreshAsync(key, getData: true, token: token).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets a value with the given key.
        /// </summary>
        /// <param name="key">The key to set the value for.</param>
        /// <param name="value">The value to set.</param>
        /// <param name="options">The cache options for the value.</param>
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var kvStore = GetKVStore().GetAwaiter().GetResult();
            var ttl = GetTTL(options);
            var entry = CreateCacheEntry(key, value, options);

            try
            {
                if (ttl.HasValue)
                {
                    kvStore.PutAsync(GetKeyPrefix(key), entry, ttl.Value, serializer: _cacheEntrySerializer, default).GetAwaiter()
                        .GetResult();
                }
                else
                {
                    kvStore.PutAsync(GetKeyPrefix(key), entry, serializer: _cacheEntrySerializer, default).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                throw;
            }
        }

        /// <summary>
        /// Asynchronously sets a value with the given key.
        /// </summary>
        /// <param name="key">The key to set the value for.</param>
        /// <param name="value">The value to set.</param>
        /// <param name="options">The cache options for the value.</param>
        /// <param name="token">Optional. A <see cref="CancellationToken" /> to cancel the operation.</param>
        /// <returns>A <see cref="Task" /> that represents the asynchronous set operation.</returns>
        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            token.ThrowIfCancellationRequested();

            var kvStore = await GetKVStore().ConfigureAwait(false);
            var ttl = GetTTL(options);
            var entry = CreateCacheEntry(key, value, options);

            if (ttl.HasValue)
            {
                await kvStore.PutAsync(GetKeyPrefix(key), entry, ttl.Value, serializer: _cacheEntrySerializer, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await kvStore.PutAsync(GetKeyPrefix(key), entry, serializer: _cacheEntrySerializer, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Refreshes a value in the cache based on its key, resetting its sliding expiration timeout (if any).
        /// </summary>
        /// <param name="key">The key to refresh.</param>
        public void Refresh(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            GetAndRefresh(key, getData: false);
        }

        /// <summary>
        /// Asynchronously refreshes a value in the cache based on its key, resetting its sliding expiration timeout (if any).
        /// </summary>
        /// <param name="key">The key to refresh.</param>
        /// <param name="token">Optional. A <see cref="CancellationToken" /> to cancel the operation.</param>
        /// <returns>A <see cref="Task" /> that represents the asynchronous refresh operation.</returns>
        public async Task RefreshAsync(string key, CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            token.ThrowIfCancellationRequested();

            await GetAndRefreshAsync(key, getData: false, token: token).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the value with the given key.
        /// </summary>
        /// <param name="key">The key to remove the value for.</param>
        public void Remove(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            GetKVStore().GetAwaiter().GetResult().DeleteAsync(GetKeyPrefix(key)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously removes the value with the given key.
        /// </summary>
        /// <param name="key">The key to remove the value for.</param>
        /// <param name="token">Optional. A <see cref="CancellationToken" /> to cancel the operation.</param>
        /// <returns>A <see cref="Task" /> that represents the asynchronous remove operation.</returns>
        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            token.ThrowIfCancellationRequested();

            var kvStore = await GetKVStore().ConfigureAwait(false);
            await kvStore.DeleteAsync(GetKeyPrefix(key), cancellationToken: token).ConfigureAwait(false);
        }

        private async Task<NatsKVStore> GetKVStore()
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
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(NatsCache));
                    }

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

        private static DistributedCacheEntryOptions GetExpirationOptions(
            string? absoluteExpiration,
            string? slidingExpiration)
        {
            var options = new DistributedCacheEntryOptions();
            if (absoluteExpiration != null)
            {
                options.AbsoluteExpiration = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(absoluteExpiration));
            }

            if (slidingExpiration != null)
            {
                options.SlidingExpiration = TimeSpan.FromMilliseconds(long.Parse(slidingExpiration));
            }

            return options;
        }

        private TimeSpan? GetTTL(DistributedCacheEntryOptions options)
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

            if (absoluteExpiration.HasValue)
            {
                var ttl = absoluteExpiration.Value - DateTimeOffset.Now;
                if (ttl.TotalMilliseconds <= 0)
                {
                    // Value is in the past, remove it
                    return TimeSpan.Zero;
                }

                // If there's also a sliding expiration, use the minimum of the two
                if (options.SlidingExpiration.HasValue)
                {
                    return TimeSpan.FromTicks(Math.Min(ttl.Ticks, options.SlidingExpiration.Value.Ticks));
                }

                return ttl;
            }

            return options.SlidingExpiration;
        }

        private CacheEntry CreateCacheEntry(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            var absoluteExpiration = options.AbsoluteExpiration;
            if (options.AbsoluteExpirationRelativeToNow.HasValue)
            {
                absoluteExpiration = DateTimeOffset.Now.Add(options.AbsoluteExpirationRelativeToNow.Value);
            }

            var cacheEntry = new CacheEntry { Data = value };

            if (absoluteExpiration.HasValue)
            {
                cacheEntry.AbsoluteExpiration = absoluteExpiration.Value.ToUnixTimeMilliseconds().ToString();
            }

            if (options.SlidingExpiration.HasValue)
            {
                cacheEntry.SlidingExpiration = options.SlidingExpiration.Value.TotalMilliseconds.ToString();
            }

            return cacheEntry;
        }

        private byte[]? GetAndRefresh(string key, bool getData)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            return GetAndRefreshAsync(key, getData).GetAwaiter().GetResult();
        }

        private async Task<byte[]?> GetAndRefreshAsync(string key, bool getData, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var kvStore = await GetKVStore().ConfigureAwait(false);
            var prefixedKey = GetKeyPrefix(key);
            try
            {
                var natsResult = await kvStore.TryGetEntryAsync<CacheEntry>(prefixedKey, serializer: _cacheEntrySerializer, cancellationToken: token)
                    .ConfigureAwait(false);
                if (!natsResult.Success)
                {
                    return null;
                }

                var kvEntry = natsResult.Value;

                // Check if the value is null
                if (kvEntry.Value == null)
                {
                    return null;
                }

                // Check absolute expiration
                if (kvEntry.Value.AbsoluteExpiration != null)
                {
                    var absoluteExpiration =
                        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(kvEntry.Value.AbsoluteExpiration));
                    if (absoluteExpiration <= DateTimeOffset.Now)
                    {
                        // NatsKVWrongLastRevisionException is caught below
                        await kvStore.DeleteAsync(
                                prefixedKey,
                                new NatsKVDeleteOpts { Revision = kvEntry.Revision },
                                cancellationToken: token)
                            .ConfigureAwait(false);

                        return null;
                    }
                }

                // Refresh if sliding expiration exists
                if (kvEntry.Value.SlidingExpiration != null)
                {
                    var slidingExpirationMilliseconds = long.Parse(kvEntry.Value.SlidingExpiration);
                    var slidingExpiration = TimeSpan.FromMilliseconds(slidingExpirationMilliseconds);

                    if (slidingExpiration > TimeSpan.Zero)
                    {
                        await UpdateEntryExpirationAsync(kvStore, prefixedKey, kvEntry, token)
                            .ConfigureAwait(false);
                    }
                }

                return getData ? kvEntry.Value.Data : null;
            }
            catch (NatsKVWrongLastRevisionException)
            {
                // Optimistic concurrency control failed, someone else updated it
                // That's fine, we just retry the get operation
                LogUpdateFailed(key);

                // Try once more to get the latest value
                try
                {
                    var natsResult = await kvStore.TryGetEntryAsync<CacheEntry>(prefixedKey, serializer: _cacheEntrySerializer, cancellationToken: token)
                        .ConfigureAwait(false);
                    if (!natsResult.Success)
                    {
                        return null;
                    }

                    var kvEntry = natsResult.Value;

                    // Check if the value is null
                    if (kvEntry.Value == null)
                    {
                        return null;
                    }

                    return getData ? kvEntry.Value.Data : null;
                }
                catch (Exception ex)
                {
                    LogException(ex);
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                throw;
            }
        }

        private async Task UpdateEntryExpirationAsync(NatsKVStore kvStore, string key, NatsKVEntry<CacheEntry> kvEntry,
            CancellationToken token)
        {
            // Calculate new TTL based on sliding expiration
            TimeSpan? ttl = null;

            // If we have a sliding expiration, use it as the TTL
            if (kvEntry.Value.SlidingExpiration != null)
            {
                ttl = TimeSpan.FromMilliseconds(long.Parse(kvEntry.Value.SlidingExpiration));

                // If we also have an absolute expiration, make sure we don't exceed it
                if (kvEntry.Value.AbsoluteExpiration != null)
                {
                    var absoluteExpiration =
                        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(kvEntry.Value.AbsoluteExpiration));
                    var remainingTime = absoluteExpiration - DateTimeOffset.Now;

                    // Use the minimum of sliding window or remaining absolute time
                    if (remainingTime > TimeSpan.Zero && remainingTime < ttl)
                    {
                        ttl = remainingTime;
                    }
                }
            }

            if (ttl.HasValue && ttl.Value > TimeSpan.Zero)
            {
                // Use optimistic concurrency control with the last revision
                try
                {
                    await kvStore.UpdateAsync(key, kvEntry.Value, kvEntry.Revision, ttl.Value, serializer: _cacheEntrySerializer, cancellationToken: token)
                        .ConfigureAwait(false);
                }
                catch (NatsKVWrongLastRevisionException)
                {
                    // Someone else updated it; that's fine, we'll get the latest version next time
                    LogUpdateFailed(key.Replace(GetKeyPrefix(string.Empty), string.Empty));
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connectionLock.Dispose();
                _disposed = true;

                // Set to null to ensure we don't use it after dispose
                _kvStore = null;
            }
        }

        /// <inheritdoc />
        public bool TryGetValue(string key, out ReadOnlySequence<byte> sequence)
        {
            sequence = ReadOnlySequence<byte>.Empty;

            try
            {
                var result = Get(key);
                if (result != null)
                {
                    sequence = new ReadOnlySequence<byte>(result);
                    return true;
                }
            }
            catch
            {
                // Ignore failures here; they will surface later
            }

            return false;
        }

        /// <inheritdoc />
        public async ValueTask<bool> TryGetValueAsync(string key, Memory<byte> destination,
            CancellationToken token = default)
        {
            try
            {
                var result = await GetAsync(key, token).ConfigureAwait(false);
                if (result != null)
                {
                    if (result.Length <= destination.Length)
                    {
                        result.CopyTo(destination);
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore failures here; they will surface later
            }

            return false;
        }

        /// <inheritdoc />
        public async ValueTask<bool> GetAsync(string key, Stream destination, CancellationToken token = default)
        {
            try
            {
                var result = await GetAsync(key, token).ConfigureAwait(false);
                if (result != null)
                {
                    await destination.WriteAsync(result, token).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                // Ignore failures here; they will surface later
            }

            return false;
        }

        /// <inheritdoc />
        public async ValueTask<bool> GetAndRefreshAsync(string key, Stream destination, bool getData,
            CancellationToken token = default)
        {
            try
            {
                var result = await GetAndRefreshAsync(key, getData, token).ConfigureAwait(false);
                if (result != null)
                {
                    await destination.WriteAsync(result, token).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                // Ignore failures here; they will surface later
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryGet(string key, IBufferWriter<byte> destination)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            try
            {
                var result = Get(key);
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

        /// <inheritdoc />
        public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination,
            CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

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

        /// <inheritdoc />
        public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            byte[] array;

            if (value.IsSingleSegment)
            {
                array = value.First.ToArray();
            }
            else
            {
                array = value.ToArray();
            }

            Set(key, array, options);
        }

        /// <inheritdoc />
        public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            token.ThrowIfCancellationRequested();

            byte[] array;

            if (value.IsSingleSegment)
            {
                array = value.First.ToArray();
            }
            else
            {
                array = value.ToArray();
            }

            await SetAsync(key, array, options, token).ConfigureAwait(false);
        }
    }
}
