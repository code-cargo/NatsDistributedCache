using System.Buffers;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Outcome of a single-copy <see cref="CacheEntry"/> read produced by
/// <see cref="BufferWritingCacheEntryDeserializer"/>. Carries just enough for the read core to finish the
/// operation without a second copy of the payload on the common path. A <see langword="null"/> result (the
/// deserializer returning <c>null</c>) means the stored bytes were undeserializable — exactly as
/// <see cref="CacheEntryBinarySerializer.Deserialize"/> signals the same condition with a null entry.
/// </summary>
internal sealed class CacheEntryBufferReadResult
{
    internal CacheEntryBufferReadResult(CacheEntry entry, bool payloadWritten, bool absolutelyExpired)
    {
        Entry = entry;
        PayloadWritten = payloadWritten;
        AbsolutelyExpired = absolutelyExpired;
    }

    /// <summary>
    /// The decoded entry. Its <see cref="CacheEntry.Data"/> is populated only when the payload was
    /// <em>not</em> streamed to the destination (the sliding-expiration case, where the read core must
    /// re-write the value to refresh its TTL); otherwise it is left null because the bytes already live in
    /// the caller's buffer.
    /// </summary>
    internal CacheEntry Entry { get; }

    /// <summary>
    /// <see langword="true"/> when the payload was written straight into the caller's
    /// <see cref="IBufferWriter{T}"/> — a confirmed hit with no sliding refresh, and the single-copy fast
    /// path. The read core only needs to record the hit.
    /// </summary>
    internal bool PayloadWritten { get; }

    /// <summary>
    /// <see langword="true"/> when the entry is past its absolute expiration. Nothing was written; the read
    /// core evicts the entry (using the KV revision) and reports a miss.
    /// </summary>
    internal bool AbsolutelyExpired { get; }
}

/// <summary>
/// A per-read <see cref="INatsDeserialize{T}"/> used only by <c>NatsCache.TryGetAsync(IBufferWriter&lt;byte&gt;)</c>
/// to avoid the intermediate <see cref="byte"/> array that the array read path allocates. It is the only
/// point in the read that holds the transport payload sequence, so it writes the payload directly into the
/// caller's destination — a single copy (transport buffer → caller buffer) instead of two (transport buffer →
/// <see cref="CacheEntry.Data"/> → caller buffer).
/// </summary>
/// <remarks>
/// The write only happens for a genuine hit, so the <see cref="IBufferWriter{T}"/> contract of "nothing
/// written on a miss" is preserved:
/// <list type="bullet">
/// <item>undeserializable framing → returns <see langword="null"/>, writes nothing;</item>
/// <item>absolutely expired → returns a result flagged expired, writes nothing (the core evicts + misses);</item>
/// <item>sliding expiration present → materializes <see cref="CacheEntry.Data"/> and writes nothing, because
/// the core must re-write the whole value to refresh its TTL and therefore needs the bytes (this is the one
/// case that still costs two copies — the common absolute-only/no-expiry hit does not);</item>
/// <item>otherwise → writes the payload to the destination and flags the hit.</item>
/// </list>
/// Absolute expiry is evaluated here rather than in the core because this is the only moment the payload is
/// available, and the buffer must stay untouched when the entry turns out to be an expired miss. It uses
/// <see cref="TimeProvider.GetUtcNow"/> at deserialize time — the same clock and effectively the same instant
/// the core would have used immediately afterward — so the decision matches <c>NatsCache.IsAbsolutelyExpired</c>.
/// </remarks>
internal sealed class BufferWritingCacheEntryDeserializer : INatsDeserialize<CacheEntryBufferReadResult>
{
    private readonly IBufferWriter<byte> _destination;
    private readonly TimeProvider _timeProvider;

    internal BufferWritingCacheEntryDeserializer(IBufferWriter<byte> destination, TimeProvider timeProvider)
    {
        _destination = destination;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public CacheEntryBufferReadResult? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!CacheEntryBinarySerializer.TryReadHeader(ref reader, out var absoluteExpiration, out var slidingExpirationTicks))
        {
            // Unknown/legacy/corrupt framing: signal an undeserializable entry (see the type doc).
            return null;
        }

        var remaining = reader.UnreadSequence;

        if (slidingExpirationTicks.HasValue)
        {
            // Sliding entries have their TTL refreshed by re-writing the value, which needs the payload
            // bytes — so this path cannot stream to the caller. Materialize the full entry (one copy) and
            // let the read core run its normal expiry/refresh/write, exactly like the array read path.
            var data = remaining.IsEmpty ? Array.Empty<byte>() : remaining.ToArray();
            return new CacheEntryBufferReadResult(
                new CacheEntry
                {
                    AbsoluteExpiration = absoluteExpiration,
                    SlidingExpirationTicks = slidingExpirationTicks,
                    Data = data,
                },
                payloadWritten: false,
                absolutelyExpired: false);
        }

        if (absoluteExpiration.HasValue && _timeProvider.GetUtcNow() >= absoluteExpiration.Value)
        {
            // Absolutely expired: write nothing. The read core evicts the entry and reports a miss.
            return new CacheEntryBufferReadResult(
                new CacheEntry { AbsoluteExpiration = absoluteExpiration },
                payloadWritten: false,
                absolutelyExpired: true);
        }

        // Genuine hit with no sliding refresh: copy the payload straight into the caller's buffer. This is
        // the single copy that makes TryGet allocation-free of any intermediate array on the common path.
        WritePayload(remaining);
        return new CacheEntryBufferReadResult(
            new CacheEntry { AbsoluteExpiration = absoluteExpiration },
            payloadWritten: true,
            absolutelyExpired: false);
    }

    private void WritePayload(in ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            if (!payload.First.IsEmpty)
            {
                _destination.Write(payload.First.Span);
            }

            return;
        }

        foreach (var segment in payload)
        {
            if (!segment.IsEmpty)
            {
                _destination.Write(segment.Span);
            }
        }
    }
}
