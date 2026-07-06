using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Serialization;

/// <summary>
/// Tests for the compact binary <see cref="CacheEntry"/> serializer.
/// </summary>
public class CacheEntryBinarySerializerTests
{
    private static readonly DateTimeOffset SampleAbsolute =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly long SampleSlidingTicks = TimeSpan.FromMinutes(10).Ticks;

    private readonly CacheEntryBinarySerializer _serializer = CacheEntryBinarySerializer.Default;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RoundTrips_AllExpirationCombinations(bool hasAbsolute, bool hasSliding)
    {
        var entry = new CacheEntry
        {
            AbsoluteExpiration = hasAbsolute ? SampleAbsolute : null,
            SlidingExpirationTicks = hasSliding ? SampleSlidingTicks : null,
            Data = [1, 2, 3, 4, 5],
        };

        var result = Deserialize(Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(entry.AbsoluteExpiration, result.AbsoluteExpiration);
        Assert.Equal(entry.SlidingExpirationTicks, result.SlidingExpirationTicks);
        Assert.Equal(entry.Data, result.Data);
    }

    [Fact]
    public void RoundTrips_EmptyData()
    {
        var entry = new CacheEntry { Data = [] };

        var result = Deserialize(Serialize(entry));

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data);
    }

    [Fact]
    public void RoundTrips_LargePayload()
    {
        var payload = new byte[64 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        var entry = new CacheEntry
        {
            AbsoluteExpiration = SampleAbsolute,
            SlidingExpirationTicks = SampleSlidingTicks,
            Data = payload,
        };

        var result = Deserialize(Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(payload, result.Data);
    }

    [Fact]
    public void Serialize_WritesVersionedHeaderAndRawPayload()
    {
        var entry = new CacheEntry
        {
            AbsoluteExpiration = SampleAbsolute,
            SlidingExpirationTicks = SampleSlidingTicks,
            Data = [10, 20, 30],
        };

        var bytes = Serialize(entry);

        // [version:1][flags:1][absTicks:8][sldTicks:8][payload:3]
        Assert.Equal(2 + 8 + 8 + 3, bytes.Length);
        Assert.Equal(CacheEntryBinarySerializer.FormatVersion, bytes[0]);
        Assert.Equal(0b0000_0011, bytes[1]);
        Assert.Equal(SampleAbsolute.UtcTicks, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(2, 8)));
        Assert.Equal(SampleSlidingTicks, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(10, 8)));
        Assert.Equal(new byte[] { 10, 20, 30 }, bytes[18..]);
    }

    [Fact]
    public void Serialize_WithoutExpiration_WritesTwoByteHeader()
    {
        var entry = new CacheEntry { Data = [42] };

        var bytes = Serialize(entry);

        Assert.Equal(3, bytes.Length);
        Assert.Equal(CacheEntryBinarySerializer.FormatVersion, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(42, bytes[2]);
    }

    [Fact]
    public void Deserialize_WrongVersion_ReturnsNull()
    {
        var bytes = Serialize(new CacheEntry { Data = [1, 2, 3] });
        bytes[0] = (byte)(CacheEntryBinarySerializer.FormatVersion + 1);

        Assert.Null(Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_LegacyJsonBytes_ReturnsNull()
    {
        // Old envelope format: a JSON object whose first byte is '{' (0x7B).
        var legacy = Encoding.UTF8.GetBytes("{\"absexp\":null,\"sldexp\":null,\"data\":\"AQID\"}");

        Assert.Null(Deserialize(legacy));
    }

    [Fact]
    public void Deserialize_EmptyBuffer_ReturnsNull() =>
        Assert.Null(Deserialize([]));

    [Fact]
    public void Deserialize_TruncatedExpirationField_ReturnsNull()
    {
        // Header claims an absolute expiration but only supplies 4 of the required 8 bytes.
        byte[] truncated = [CacheEntryBinarySerializer.FormatVersion, 0b0000_0001, 1, 2, 3, 4];

        Assert.Null(Deserialize(truncated));
    }

    [Fact]
    public void Deserialize_UnknownFlagBits_ReturnsNull()
    {
        // A v1 entry with an undefined flag bit set (0b100) must fail closed rather than treat the
        // remaining bytes as payload.
        byte[] bytes = [CacheEntryBinarySerializer.FormatVersion, 0b0000_0100, 1, 2, 3];

        Assert.Null(Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_OutOfRangeAbsoluteTicks_ReturnsNull()
    {
        // flags = HasAbsoluteExpiration, followed by a tick value beyond DateTimeOffset's range.
        var bytes = new byte[2 + 8];
        bytes[0] = CacheEntryBinarySerializer.FormatVersion;
        bytes[1] = 0b0000_0001;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), long.MaxValue);

        Assert.Null(Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_OutOfRangeSlidingTicks_ReturnsNull()
    {
        // flags = HasSlidingExpiration, followed by an absurd sliding tick value. Before validation
        // this deserialized "successfully" and yielded a ~10,675-day TTL; it must now fail closed.
        Assert.Null(Deserialize(SlidingOnly(long.MaxValue)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Deserialize_NonPositiveSlidingTicks_ReturnsNull(long slidingTicks)
    {
        // A valid sliding window is always a positive TimeSpan, so zero/negative ticks are corrupt.
        Assert.Null(Deserialize(SlidingOnly(slidingTicks)));
    }

    [Fact]
    public void Deserialize_TruncatedSlidingField_ReturnsNull()
    {
        // Header claims a sliding expiration but only supplies 4 of the required 8 bytes.
        byte[] truncated = [CacheEntryBinarySerializer.FormatVersion, 0b0000_0010, 1, 2, 3, 4];

        Assert.Null(Deserialize(truncated));
    }

    [Fact]
    public void Deserialize_MaxAcceptedSlidingTicks_RoundTrips()
    {
        // The upper bound (the full DateTimeOffset tick range) is inclusive and still deserializes.
        var result = Deserialize(SlidingOnly(DateTimeOffset.MaxValue.Ticks));

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.MaxValue.Ticks, result.SlidingExpirationTicks);
    }

    [Fact]
    public void Deserialize_MultiSegmentSequence_RoundTrips()
    {
        var entry = new CacheEntry
        {
            AbsoluteExpiration = SampleAbsolute,
            SlidingExpirationTicks = SampleSlidingTicks,
            Data = [7, 8, 9, 10],
        };
        var bytes = Serialize(entry);

        // Split mid-header so the reader must cross a segment boundary.
        var sequence = CreateSegmented(bytes, splitAt: 5);
        var result = _serializer.Deserialize(sequence);

        Assert.NotNull(result);
        Assert.Equal(entry.AbsoluteExpiration, result.AbsoluteExpiration);
        Assert.Equal(entry.SlidingExpirationTicks, result.SlidingExpirationTicks);
        Assert.Equal(entry.Data, result.Data);
    }

    [Fact]
    public void AbsoluteExpiration_RoundTripsAsUtcInstant()
    {
        // A non-UTC offset must survive as the same instant, normalized to UTC.
        var local = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        var entry = new CacheEntry { AbsoluteExpiration = local, Data = [1] };

        var result = Deserialize(Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(local.UtcTicks, result.AbsoluteExpiration!.Value.UtcTicks);
        Assert.Equal(TimeSpan.Zero, result.AbsoluteExpiration.Value.Offset);
        Assert.Equal(local.UtcDateTime, result.AbsoluteExpiration.Value.UtcDateTime);
    }

    // Builds a sliding-expiration-only entry: [version][flags=HasSlidingExpiration][slidingTicks:8].
    private static byte[] SlidingOnly(long slidingTicks)
    {
        var bytes = new byte[2 + 8];
        bytes[0] = CacheEntryBinarySerializer.FormatVersion;
        bytes[1] = 0b0000_0010;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), slidingTicks);
        return bytes;
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] data, int splitAt)
    {
        var first = new BufferSegment(data.AsMemory(0, splitAt));
        var second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private byte[] Serialize(CacheEntry entry)
    {
        var writer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(writer, entry);
        return writer.WrittenMemory.ToArray();
    }

    private CacheEntry? Deserialize(byte[] bytes) =>
        _serializer.Deserialize(new ReadOnlySequence<byte>(bytes));

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
