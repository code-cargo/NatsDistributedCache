using System.Buffers;
using BenchmarkDotNet.Attributes;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Baseline serialization benchmarks for the cache envelope. This measures the current
/// JSON+base64 envelope; issue #37 adds a compact binary format and the head-to-head comparison.
/// <c>MemoryDiagnoser</c> reports per-operation allocations; the <c>--sizes</c> report
/// (see <see cref="SizeReport"/>) shows the stored byte counts.
/// </summary>
[MemoryDiagnoser]
public class CacheEntrySerializationBenchmarks
{
    private static readonly NatsJsonContextSerializer<JsonCacheEntry> JsonEnvelopeSerializer =
        new(BenchmarkJsonContext.Default);

    private readonly ArrayBufferWriter<byte> _writer = new();

    private JsonCacheEntry _jsonEntry = null!;
    private ReadOnlySequence<byte> _jsonBytes;

    /// <summary>
    /// Gets or sets the size, in bytes, of the cached payload under test.
    /// </summary>
    [Params(128, 1024, 8192)]
    public int PayloadSize { get; set; }

    /// <summary>
    /// Builds the sample entry and pre-serialized buffer used by the benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _jsonEntry = BenchmarkData.CreateJsonEntry(PayloadSize);
        _jsonBytes = ToSequence(JsonEnvelopeSerializer, _jsonEntry);
    }

    /// <summary>
    /// Serializes the entry using the JSON envelope.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark]
    public int Json_Serialize()
    {
        _writer.Clear();
        JsonEnvelopeSerializer.Serialize(_writer, _jsonEntry);
        return _writer.WrittenCount;
    }

    /// <summary>
    /// Deserializes a JSON envelope.
    /// </summary>
    /// <returns>The decoded entry.</returns>
    [Benchmark]
    public JsonCacheEntry? Json_Deserialize() => JsonEnvelopeSerializer.Deserialize(_jsonBytes);

    private static ReadOnlySequence<byte> ToSequence<T>(INatsSerialize<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
    }
}
