using NATS.Client.JetStream.Models;

namespace CodeCargo.Nats.DistributedCache;

public partial class NatsCache : INatsCacheMaintenance
{
    /// <inheritdoc />
    public async Task<long> PurgeByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // Reject an empty/whitespace prefix outright. Without it the subject filter below would collapse to
        // the entire configured-prefix space (or, with no CacheKeyPrefix, the whole bucket), turning a scoped
        // maintenance call into an accidental full purge.
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Prefix must not be null, empty, or whitespace.", nameof(prefix));
        }

        // Trailing '.' is trimmed to mirror the constructor's normalization of _keyPrefix, and so the raw
        // prefix never ends in '.'. That matters because the key encoder only escapes a trailing '.' at the
        // very end of a string; leaving one on the prefix would encode the separator differently here than it
        // is encoded mid-key inside a stored full key, and the filter would stop matching.
        var trimmedPrefix = prefix.TrimEnd('.');
        if (trimmedPrefix.Length == 0)
        {
            throw new ArgumentException("Prefix must not consist solely of '.' characters.", nameof(prefix));
        }

        // Compose the caller's sub-prefix with the configured CacheKeyPrefix exactly as GetEncodedKey composes
        // a full key, so the raw prefix is a genuine leading segment of every stored key beneath it.
        var rawPrefix = string.IsNullOrEmpty(_keyPrefix) ? trimmedPrefix : $"{_keyPrefix}.{trimmedPrefix}";

        // The encoder URL-encodes per character and leaves '.' unescaped (it is RFC 3986 unreserved) while
        // escaping the NATS wildcards '*' and '>', so the encoded prefix is a byte-for-byte leading segment of
        // every encoded full key beneath it and cannot itself contain a wildcard. A KV key is stored on the
        // subject '$KV.<bucket>.<encodedKey>', so appending the NATS multi-token wildcard '>' after the '.'
        // separator yields a subject filter that matches every child of the prefix namespace. The cache never
        // stores a bare-prefix key (keys are always '{prefix}.{userKey}'), so scoping the filter to children
        // with '$KV.<bucket>.{encodedPrefix}.>' -- rather than also matching a key exactly equal to the prefix
        // -- is correct.
        var encodedPrefix = _keyEncoder.Encode(rawPrefix);
        var subjectFilter = $"$KV.{_bucketName}.{encodedPrefix}.>";

        var store = await GetKvStore().ConfigureAwait(false);

        // Purge every matching message from the KV bucket's backing JetStream stream ('KV_<bucket>') in a
        // single server round-trip, scoped to the prefix's subject space. This deletes the messages outright
        // rather than enumerating the keys and issuing a per-key KV purge -- which would take N round-trips and
        // leave purge-marker tombstones that linger until a PurgeDeletes() compaction. With the bucket's
        // single-revision history (History = 1) there is exactly one message per live key, so the returned
        // Purged count is the number of entries removed.
        var response = await store.JetStreamContext
            .PurgeStreamAsync(
                $"KV_{_bucketName}",
                new StreamPurgeRequest { Filter = subjectFilter },
                cancellationToken)
            .ConfigureAwait(false);

        return response.Purged;
    }
}
