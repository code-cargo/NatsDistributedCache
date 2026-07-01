using System.Buffers;
using BenchmarkDotNet.Attributes;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Compares the legacy JSON+base64 envelope against the compact binary framing for serializing and
/// deserializing a <see cref="CacheEntry"/>. <c>MemoryDiagnoser</c> reports the per-operation
/// allocations; the <c>--sizes</c> report (see <see cref="SizeReport"/>) shows the stored byte counts.
/// </summary>
[MemoryDiagnoser]
public class CacheEntrySerializationBenchmarks
{
    private static readonly NatsJsonContextSerializer<JsonCacheEntry> JsonEnvelopeSerializer =
        new(BenchmarkJsonContext.Default);

    private static readonly CacheEntryBinarySerializer BinaryEnvelopeSerializer = CacheEntryBinarySerializer.Default;

    private readonly ArrayBufferWriter<byte> _writer = new();

    private JsonCacheEntry _jsonEntry = null!;
    private CacheEntry _binaryEntry = null!;
    private ReadOnlySequence<byte> _jsonBytes;
    private ReadOnlySequence<byte> _binaryBytes;

    /// <summary>
    /// Gets or sets the size, in bytes, of the cached payload under test.
    /// </summary>
    [Params(128, 1024, 8192, 65536, 262144)]
    public int PayloadSize { get; set; }

    /// <summary>
    /// Builds the sample entries and pre-serialized buffers used by the benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _jsonEntry = BenchmarkData.CreateJsonEntry(PayloadSize);
        _binaryEntry = BenchmarkData.CreateBinaryEntry(PayloadSize);
        _jsonBytes = ToSequence(JsonEnvelopeSerializer, _jsonEntry);
        _binaryBytes = ToSequence(BinaryEnvelopeSerializer, _binaryEntry);
    }

    /// <summary>
    /// Serializes the entry using the legacy JSON envelope (baseline).
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark(Baseline = true)]
    public int Json_Serialize()
    {
        _writer.Clear();
        JsonEnvelopeSerializer.Serialize(_writer, _jsonEntry);
        return _writer.WrittenCount;
    }

    /// <summary>
    /// Serializes the entry using the compact binary framing.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark]
    public int Binary_Serialize()
    {
        _writer.Clear();
        BinaryEnvelopeSerializer.Serialize(_writer, _binaryEntry);
        return _writer.WrittenCount;
    }

    /// <summary>
    /// Deserializes a JSON envelope (baseline read path).
    /// </summary>
    /// <returns>The decoded entry.</returns>
    [Benchmark]
    public JsonCacheEntry? Json_Deserialize() => JsonEnvelopeSerializer.Deserialize(_jsonBytes);

    /// <summary>
    /// Deserializes a binary envelope.
    /// </summary>
    /// <returns>The decoded entry.</returns>
    [Benchmark]
    public CacheEntry? Binary_Deserialize() => BinaryEnvelopeSerializer.Deserialize(_binaryBytes);

    private static ReadOnlySequence<byte> ToSequence<T>(INatsSerialize<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
    }
}
