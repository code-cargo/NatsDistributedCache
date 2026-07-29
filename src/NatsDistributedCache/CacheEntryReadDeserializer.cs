using System.Buffers;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// What a single <see cref="CacheEntry"/> read produced, as seen by <c>NatsCache</c>'s read core. A
/// <see langword="null"/> result (the deserializer returning <c>null</c>) means the stored bytes were
/// undeserializable — the same way <see cref="CacheEntryBinarySerializer.Deserialize"/> signals it with a
/// null entry.
/// </summary>
internal enum CacheEntryReadOutcome
{
    /// <summary>
    /// The payload was materialized into <see cref="CacheEntryReadResult.Entry"/>'s <see cref="CacheEntry.Data"/>.
    /// The read core checks absolute expiry, refreshes a sliding TTL, then emits the payload — returning it for
    /// the array path, or writing it to the destination for a sliding buffer read. Used for every array read
    /// and for sliding entries on the buffer path.
    /// </summary>
    Materialized,

    /// <summary>
    /// Buffer fast path: the payload was written straight into the caller's <see cref="IBufferWriter{T}"/>.
    /// A confirmed hit; the core only records it.
    /// </summary>
    PayloadWritten,

    /// <summary>
    /// Buffer fast path: the entry is past its absolute expiration. Nothing was written; the core evicts the
    /// entry and reports a miss.
    /// </summary>
    AbsolutelyExpired,

    /// <summary>
    /// The caller's <see cref="IBufferWriter{T}"/> threw while the payload was being written (e.g. HybridCache's
    /// payload quota). Not corrupt data: the exception is carried out so the core surfaces an error instead of
    /// an undeserializable miss.
    /// </summary>
    DestinationFailure,
}

/// <summary>
/// Outcome of a single read produced by <see cref="CacheEntryReadDeserializer"/>. The reusable
/// <see cref="PayloadWritten"/> and <see cref="AbsolutelyExpired"/> singletons let the common buffer hit and
/// the expired path allocate nothing beyond the payload itself; only the materialized and destination-failure
/// outcomes carry per-read state.
/// </summary>
internal sealed class CacheEntryReadResult
{
    internal static readonly CacheEntryReadResult PayloadWritten =
        new(CacheEntryReadOutcome.PayloadWritten, entry: null, destinationFailure: null);

    internal static readonly CacheEntryReadResult AbsolutelyExpired =
        new(CacheEntryReadOutcome.AbsolutelyExpired, entry: null, destinationFailure: null);

    private CacheEntryReadResult(CacheEntryReadOutcome outcome, CacheEntry? entry, Exception? destinationFailure)
    {
        Outcome = outcome;
        Entry = entry;
        DestinationFailure = destinationFailure;
    }

    internal CacheEntryReadOutcome Outcome { get; }

    /// <summary>The decoded entry, non-null only for <see cref="CacheEntryReadOutcome.Materialized"/>.</summary>
    internal CacheEntry? Entry { get; }

    /// <summary>The writer exception, non-null only for <see cref="CacheEntryReadOutcome.DestinationFailure"/>.</summary>
    internal Exception? DestinationFailure { get; }

    internal static CacheEntryReadResult Materialized(CacheEntry entry) =>
        new(CacheEntryReadOutcome.Materialized, entry, destinationFailure: null);

    internal static CacheEntryReadResult DestinationFailed(Exception exception) =>
        new(CacheEntryReadOutcome.DestinationFailure, entry: null, exception);
}

/// <summary>
/// The per-read <see cref="INatsDeserialize{T}"/> used by <c>NatsCache</c>'s unified read core.
/// </summary>
/// <remarks>
/// With a <see langword="null"/> destination it materializes the entry for the array read path (Get/Refresh).
/// With a destination it powers the single-copy <c>TryGetAsync(IBufferWriter&lt;byte&gt;)</c> path: on a genuine
/// hit with no sliding refresh it writes the payload straight into the caller's writer, so the read costs one
/// copy (transport buffer -> caller buffer) instead of two. Nothing is written for a miss (undeserializable
/// framing or absolute expiry) or when the entry needs its sliding TTL refreshed — that re-writes the value and
/// so needs the bytes, which are materialized for the core to handle. If the caller's writer throws mid-write
/// the exception is carried out as <see cref="CacheEntryReadOutcome.DestinationFailure"/> so the core surfaces
/// an error rather than an undeserializable miss; on a multi-segment payload any segments already written stay
/// committed, so the buffer contents are unspecified when a write fails.
/// </remarks>
internal sealed class CacheEntryReadDeserializer : INatsDeserialize<CacheEntryReadResult>
{
    private readonly IBufferWriter<byte>? _destination;
    private readonly TimeProvider _timeProvider;

    internal CacheEntryReadDeserializer(IBufferWriter<byte>? destination, TimeProvider timeProvider)
    {
        _destination = destination;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public CacheEntryReadResult? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        // Array read path: no destination to stream into, so materialize the whole entry via the canonical
        // deserializer and let the read core decide expiry/refresh/eviction.
        if (_destination is null)
        {
            var entry = CacheEntryBinarySerializer.Default.Deserialize(buffer);
            return entry is null ? null : CacheEntryReadResult.Materialized(entry);
        }

        var reader = new SequenceReader<byte>(buffer);
        if (!CacheEntryBinarySerializer.TryReadHeader(ref reader, out var absoluteExpiration, out var slidingExpirationTicks))
        {
            // Unknown/legacy/corrupt framing: signal an undeserializable entry.
            return null;
        }

        var remaining = reader.UnreadSequence;

        if (slidingExpirationTicks.HasValue)
        {
            // Sliding entries have their TTL refreshed by re-writing the value, which needs the payload
            // bytes, so this path cannot stream to the caller. Materialize and let the core handle it,
            // exactly like the array path.
            var data = remaining.IsEmpty ? Array.Empty<byte>() : remaining.ToArray();
            return CacheEntryReadResult.Materialized(new CacheEntry
            {
                AbsoluteExpiration = absoluteExpiration,
                SlidingExpirationTicks = slidingExpirationTicks,
                Data = data,
            });
        }

        if (NatsCache.IsAbsolutelyExpired(absoluteExpiration, _timeProvider.GetUtcNow()))
        {
            // Absolutely expired: write nothing. The core evicts and reports a miss. Expiry is evaluated
            // here (the only point holding the payload) so the buffer stays untouched for an expired miss,
            // using the same clock and predicate the core would.
            return CacheEntryReadResult.AbsolutelyExpired;
        }

        // Genuine hit with no sliding refresh: copy the payload straight into the caller's buffer — the
        // single copy that makes TryGet free of any intermediate array on the common path. A writer failure
        // (e.g. HybridCache's payload quota) is not corrupt data, so carry it out for the core to surface.
        try
        {
            WritePayload(remaining);
        }
        catch (Exception ex)
        {
            return CacheEntryReadResult.DestinationFailed(ex);
        }

        return CacheEntryReadResult.PayloadWritten;
    }

    private void WritePayload(in ReadOnlySequence<byte> payload)
    {
        // The enumerator is a struct, and it yields the single segment for a single-segment sequence, so this
        // covers both without a special case.
        foreach (var segment in payload)
        {
            if (!segment.IsEmpty)
            {
                _destination!.Write(segment.Span);
            }
        }
    }
}
