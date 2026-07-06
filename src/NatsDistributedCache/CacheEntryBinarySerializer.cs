using System.Buffers;
using System.Buffers.Binary;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Serializes and deserializes <see cref="CacheEntry"/> using a compact binary framing that avoids the
/// base64 inflation and JSON overhead of the previous envelope.
/// </summary>
/// <remarks>
/// Wire format (little-endian):
/// <code>
/// [version:1][flags:1][absExpTicks:8?][sldExpTicks:8?][payload...]
/// </code>
/// The 8-byte expiration fields are only present when their corresponding flag bit is set, so an entry
/// without expiration carries just a 2-byte header. Any buffer whose leading version byte does not match
/// <see cref="FormatVersion"/> — including pre-existing JSON entries, whose first byte is '{' (0x7B) —
/// deserializes to <c>null</c> and is treated as a cache miss.
/// </remarks>
internal sealed class CacheEntryBinarySerializer : INatsSerialize<CacheEntry>, INatsDeserialize<CacheEntry>
{
    /// <summary>
    /// Current on-wire format version. Bump when the framing changes so that entries written by an older
    /// version are treated as cache misses instead of being misinterpreted.
    /// </summary>
    internal const byte FormatVersion = 1;

    /// <summary>
    /// The largest expiration window the cache can represent, in ticks. NATS message TTLs are encoded as
    /// whole seconds in a 32-bit field (<c>(int)ttl.TotalSeconds</c> in <c>NatsExtensions.ToTtlString</c>),
    /// so a window beyond <see cref="int.MaxValue"/> seconds (~68 years) would overflow the cast and emit
    /// an invalid TTL header. <c>NatsCache.GetTtl</c> rejects sliding and absolute expirations that exceed
    /// this on write, and the deserializer fails closed on stored sliding ticks above it, so every
    /// accepted value round-trips end to end and no legitimately written entry becomes an
    /// undeserializable miss.
    /// </summary>
    internal const long MaxTtlTicks = int.MaxValue * TimeSpan.TicksPerSecond;

    private const byte HasAbsoluteExpiration = 0b0000_0001;
    private const byte HasSlidingExpiration = 0b0000_0010;
    private const byte KnownFlags = HasAbsoluteExpiration | HasSlidingExpiration;

    /// <summary>
    /// Gets the shared, stateless serializer instance.
    /// </summary>
    public static CacheEntryBinarySerializer Default { get; } = new();

    /// <inheritdoc />
    public void Serialize(IBufferWriter<byte> bufferWriter, CacheEntry value)
    {
        var hasAbsolute = value.AbsoluteExpiration.HasValue;
        var hasSliding = value.SlidingExpirationTicks.HasValue;

        var flags = (byte)0;
        if (hasAbsolute)
        {
            flags |= HasAbsoluteExpiration;
        }

        if (hasSliding)
        {
            flags |= HasSlidingExpiration;
        }

        var headerLength = 2 + (hasAbsolute ? 8 : 0) + (hasSliding ? 8 : 0);
        var header = bufferWriter.GetSpan(headerLength);
        header[0] = FormatVersion;
        header[1] = flags;

        var offset = 2;
        if (hasAbsolute)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset), value.AbsoluteExpiration!.Value.UtcTicks);
            offset += 8;
        }

        if (hasSliding)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(offset), value.SlidingExpirationTicks!.Value);
            offset += 8;
        }

        bufferWriter.Advance(offset);

        if (value.Data is { Length: > 0 } data)
        {
            bufferWriter.Write(data);
        }
    }

    /// <inheritdoc />
    public CacheEntry? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryRead(out var version) || version != FormatVersion)
        {
            // Unknown or legacy (e.g. JSON) entry: treat as a cache miss.
            return null;
        }

        if (!reader.TryRead(out var flags) || (flags & ~KnownFlags) != 0)
        {
            // Unknown flag bits: corrupt data, or a future format that mistakenly reused this version.
            // Fail closed rather than misinterpreting the remaining bytes.
            return null;
        }

        DateTimeOffset? absoluteExpiration = null;
        if ((flags & HasAbsoluteExpiration) != 0)
        {
            if (!reader.TryReadLittleEndian(out long absoluteTicks) ||
                absoluteTicks < DateTimeOffset.MinValue.Ticks ||
                absoluteTicks > DateTimeOffset.MaxValue.Ticks)
            {
                // Missing or out-of-range ticks (corrupt entry): treat as a miss instead of throwing.
                return null;
            }

            absoluteExpiration = new DateTimeOffset(absoluteTicks, TimeSpan.Zero);
        }

        long? slidingExpirationTicks = null;
        if ((flags & HasSlidingExpiration) != 0)
        {
            if (!reader.TryReadLittleEndian(out long slidingTicks) ||
                slidingTicks <= 0 ||
                slidingTicks > MaxTtlTicks)
            {
                // Missing, non-positive, or out-of-range sliding ticks (corrupt entry): fail closed to
                // a miss, like the absolute-ticks bounds check above. A valid sliding window is always
                // a positive TimeSpan within (0, MaxTtlTicks]; the write path enforces the same ceiling,
                // so any legitimately written value round-trips.
                return null;
            }

            slidingExpirationTicks = slidingTicks;
        }

        var remaining = reader.UnreadSequence;
        var data = remaining.IsEmpty ? Array.Empty<byte>() : remaining.ToArray();

        return new CacheEntry
        {
            AbsoluteExpiration = absoluteExpiration,
            SlidingExpirationTicks = slidingExpirationTicks,
            Data = data,
        };
    }
}
