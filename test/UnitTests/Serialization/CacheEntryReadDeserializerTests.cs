using System.Buffers;
using CodeCargo.Nats.DistributedCache.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Serialization;

/// <summary>
/// Tests for <see cref="CacheEntryReadDeserializer"/>: array-mode materialization, the buffer fast path's
/// single-copy write, and the miss/expiry/sliding/destination-failure outcomes it reports to the read core.
/// </summary>
public class CacheEntryReadDeserializerTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // First byte is not the format version, so the header fails to parse.
    private static readonly byte[] BadFraming = [0xFF, 1, 2, 3];

    [Fact]
    public void ArrayMode_MaterializesEntry()
    {
        var entry = new CacheEntry { AbsoluteExpiration = Now.AddHours(1), Data = [1, 2, 3] };

        var result = Deserialize(destination: null, Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.Materialized, result.Outcome);
        Assert.NotNull(result.Entry);
        Assert.Equal(entry.AbsoluteExpiration, result.Entry.AbsoluteExpiration);
        Assert.Equal(entry.Data, result.Entry.Data);
    }

    [Fact]
    public void ArrayMode_BadFramingReturnsNull() =>
        Assert.Null(Deserialize(destination: null, BadFraming));

    [Fact]
    public void BufferMode_HitWritesPayloadAndReportsPayloadWritten()
    {
        var entry = new CacheEntry { Data = [10, 20, 30] };
        var destination = new ArrayBufferWriter<byte>();

        var result = Deserialize(destination, Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.PayloadWritten, result.Outcome);
        Assert.Equal(entry.Data, destination.WrittenSpan.ToArray());
    }

    [Fact]
    public void BufferMode_MultiSegmentPayloadIsWrittenWhole()
    {
        var entry = new CacheEntry { AbsoluteExpiration = Now.AddHours(1), Data = [1, 2, 3, 4, 5, 6] };
        var bytes = Serialize(entry);
        var destination = new ArrayBufferWriter<byte>();

        // Split within the payload so WritePayload must cross a segment boundary.
        var result = Deserialize(destination, Segmented(bytes, splitAt: bytes.Length - 3));

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.PayloadWritten, result.Outcome);
        Assert.Equal(entry.Data, destination.WrittenSpan.ToArray());
    }

    [Fact]
    public void BufferMode_BadFramingReturnsNullAndWritesNothing()
    {
        var destination = new ArrayBufferWriter<byte>();

        Assert.Null(Deserialize(destination, BadFraming));
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public void BufferMode_AbsolutelyExpiredAtBoundaryWritesNothing()
    {
        // Absolute instant exactly equal to "now": the inclusive >= boundary treats it as expired.
        var entry = new CacheEntry { AbsoluteExpiration = Now, Data = [1, 2, 3] };
        var destination = new ArrayBufferWriter<byte>();

        var result = Deserialize(destination, Serialize(entry), now: Now);

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.AbsolutelyExpired, result.Outcome);
        Assert.Equal(0, destination.WrittenCount);
    }

    [Fact]
    public void BufferMode_OneTickBeforeExpiryWritesPayload()
    {
        var entry = new CacheEntry { AbsoluteExpiration = Now.AddTicks(1), Data = [7] };
        var destination = new ArrayBufferWriter<byte>();

        var result = Deserialize(destination, Serialize(entry), now: Now);

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.PayloadWritten, result.Outcome);
        Assert.Equal(entry.Data, destination.WrittenSpan.ToArray());
    }

    [Fact]
    public void BufferMode_SlidingEntryIsMaterializedNotWritten()
    {
        var entry = new CacheEntry { SlidingExpirationTicks = TimeSpan.FromMinutes(5).Ticks, Data = [4, 5, 6] };
        var destination = new ArrayBufferWriter<byte>();

        var result = Deserialize(destination, Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.Materialized, result.Outcome);
        Assert.Equal(0, destination.WrittenCount); // core handles the refresh and the write for sliding entries
        Assert.NotNull(result.Entry);
        Assert.Equal(entry.SlidingExpirationTicks, result.Entry.SlidingExpirationTicks);
        Assert.Equal(entry.Data, result.Entry.Data);
    }

    [Fact]
    public void BufferMode_DestinationFailureCarriesTheException()
    {
        var entry = new CacheEntry { Data = [1, 2, 3, 4] };
        var destination = new QuotaBufferWriter(maxLength: 0);

        var result = Deserialize(destination, Serialize(entry));

        Assert.NotNull(result);
        Assert.Equal(CacheEntryReadOutcome.DestinationFailure, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.DestinationFailure);
        Assert.Equal(0, destination.WrittenCount);
    }

    private static byte[] Serialize(CacheEntry entry)
    {
        var writer = new ArrayBufferWriter<byte>();
        CacheEntryBinarySerializer.Default.Serialize(writer, entry);
        return writer.WrittenMemory.ToArray();
    }

    private static CacheEntryReadResult? Deserialize(IBufferWriter<byte>? destination, byte[] bytes, DateTimeOffset? now = null) =>
        Deserialize(destination, new ReadOnlySequence<byte>(bytes), now);

    private static CacheEntryReadResult? Deserialize(
        IBufferWriter<byte>? destination,
        ReadOnlySequence<byte> sequence,
        DateTimeOffset? now = null)
    {
        var deserializer = new CacheEntryReadDeserializer(destination, new FakeTimeProvider(now ?? Now));
        return deserializer.Deserialize(sequence);
    }

    private static ReadOnlySequence<byte> Segmented(byte[] data, int splitAt)
    {
        var first = new BufferSegment(data.AsMemory(0, splitAt));
        var second = first.Append(data.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

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
