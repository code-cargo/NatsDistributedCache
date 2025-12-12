using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace CodeCargo.Nats.DistributedCache;

public static class NatsExtensions
{
    private const string NatsTtl = "Nats-TTL";
    private static readonly Regex ValidKeyRegex = new(pattern: @"\A[-/_=\.a-zA-Z0-9]+\z", RegexOptions.Compiled);
    private static readonly NatsKVException KeyCannotBeEmptyException = new("Key cannot be empty");
    private static readonly NatsKVException KeyCannotStartOrEndWithPeriodException = new("Key cannot start or end with a period");
    private static readonly NatsKVException KeyContainsInvalidCharactersException = new("Key contains invalid characters");

    /// <summary>
    /// Put a value into the bucket using the key
    /// </summary>
    /// <param name="store">NATS key-value store instance</param>
    /// <param name="key">Key of the entry</param>
    /// <param name="value">Value of the entry</param>
    /// <param name="ttl">Time-to-live value</param>
    /// <param name="serializer">Serializer to use for the message type.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> used to cancel the API call.</param>
    /// <typeparam name="T">Serialized value type</typeparam>
    /// <returns>Revision number</returns>
    /// <remarks>
    /// TTLs should only be used when the store is configured with a storage type that supports expiration,
    /// and with history set to 1. Otherwise, the TTL behavior is undefined.
    /// History is set to 1 by default, so you should be fine unless you changed it explicitly.
    /// </remarks>
    public static async ValueTask<ulong> PutWithTtlAsync<T>(this INatsKVStore store, string key, T value, TimeSpan ttl = default, INatsSerialize<T>? serializer = default, CancellationToken cancellationToken = default)
    {
        var result = await TryPutWithTtlAsync(store, key, value, ttl, serializer, cancellationToken);
        if (!result.Success)
        {
            ThrowException(result.Error);
        }

        return result.Value;
    }

    /// <summary>
    /// Put a value into the bucket using the key
    /// </summary>
    /// <param name="store">NATS key-value store instance</param>
    /// <param name="key">Key of the entry</param>
    /// <param name="value">Value of the entry</param>
    /// <param name="ttl">Time-to-live value</param>
    /// <param name="serializer">Serializer to use for the message type.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> used to cancel the API call.</param>
    /// <typeparam name="T">Serialized value type</typeparam>
    /// <returns>Revision number</returns>
    /// <remarks>
    /// TTLs should only be used when the store is configured with a storage type that supports expiration,
    /// and with history set to 1. Otherwise, the TTL behavior is undefined.
    /// History is set to 1 by default, so you should be fine unless you changed it explicitly.
    /// </remarks>
    public static async ValueTask<NatsResult<ulong>> TryPutWithTtlAsync<T>(this INatsKVStore store, string key, T value, TimeSpan ttl = default, INatsSerialize<T>? serializer = default, CancellationToken cancellationToken = default)
    {
        var keyValidResult = TryValidateKey(key);
        if (!keyValidResult.Success)
        {
            return keyValidResult.Error;
        }

        NatsHeaders? headers = default;
        if (ttl != default)
        {
            headers = new NatsHeaders
            {
                { NatsTtl, ToTtlString(ttl) },
            };
        }

        var publishResult = await store.JetStreamContext.TryPublishAsync($"$KV.{store.Bucket}.{key}", value, serializer: serializer, headers: headers, cancellationToken: cancellationToken);
        if (publishResult.Success)
        {
            var ack = publishResult.Value;
            if (ack.Error != null)
            {
                return new NatsJSApiException(ack.Error);
            }
            else if (ack.Duplicate)
            {
                return new NatsJSDuplicateMessageException(ack.Seq);
            }

            return ack.Seq;
        }
        else
        {
            return publishResult.Error;
        }
    }

    /// <summary>
    /// Valid keys are \A[-/_=\.a-zA-Z0-9]+\z, additionally they may not start or end in .
    /// </summary>
    private static NatsResult TryValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return KeyCannotBeEmptyException;
        }

        if (key[0] == '.' || key[^1] == '.')
        {
            return KeyCannotStartOrEndWithPeriodException;
        }

        if (!ValidKeyRegex.IsMatch(key))
        {
            return KeyContainsInvalidCharactersException;
        }

        return NatsResult.Default;
    }

    /// <summary>
    /// For the TTL header, we need to convert the TimeSpan to a Go time.ParseDuration string.
    /// </summary>
    /// <param name="ttl">TTL</param>
    /// <returns>String representing the number of seconds Go time.ParseDuration() can understand.</returns>
    private static string ToTtlString(TimeSpan ttl)
        => ttl == TimeSpan.MaxValue ? "never" : $"{(int)ttl.TotalSeconds:D}s";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowException(Exception exception) => throw exception;
}
