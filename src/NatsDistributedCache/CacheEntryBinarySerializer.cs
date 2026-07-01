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

    private const byte HasAbsoluteExpiration = 0b0000_0001;
    private const byte HasSlidingExpiration = 0b0000_0010;

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

        if (!reader.TryRead(out var flags))
        {
            return null;
        }

        DateTimeOffset? absoluteExpiration = null;
        if ((flags & HasAbsoluteExpiration) != 0)
        {
            if (!reader.TryReadLittleEndian(out long absoluteTicks))
            {
                return null;
            }

            absoluteExpiration = new DateTimeOffset(absoluteTicks, TimeSpan.Zero);
        }

        long? slidingExpirationTicks = null;
        if ((flags & HasSlidingExpiration) != 0)
        {
            if (!reader.TryReadLittleEndian(out long slidingTicks))
            {
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
