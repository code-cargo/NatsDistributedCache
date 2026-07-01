using System.Buffers;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Prints the serialized envelope size (in bytes) of the JSON and binary formats for each payload size,
/// so the byte-size reduction can be inspected without running the full timing benchmarks.
/// </summary>
internal static class SizeReport
{
    private static readonly int[] PayloadSizes = [128, 1024, 8192, 65536, 262144];

    /// <summary>
    /// Writes the size-comparison table to the console.
    /// </summary>
    public static void Print()
    {
        var jsonSerializer = new NatsJsonContextSerializer<JsonCacheEntry>(BenchmarkJsonContext.Default);
        var binarySerializer = CacheEntryBinarySerializer.Default;

        Console.WriteLine("Stored envelope size (bytes) — absolute + sliding expiration set");
        Console.WriteLine($"{"Payload",10} {"JSON",10} {"Binary",10} {"Saved",10} {"Binary/JSON",12}");
        Console.WriteLine(new string('-', 56));

        foreach (var size in PayloadSizes)
        {
            var jsonSize = Measure(jsonSerializer, BenchmarkData.CreateJsonEntry(size));
            var binarySize = Measure(binarySerializer, BenchmarkData.CreateBinaryEntry(size));
            var ratio = (double)binarySize / jsonSize;
            Console.WriteLine($"{size,10} {jsonSize,10} {binarySize,10} {jsonSize - binarySize,10} {ratio,12:P1}");
        }
    }

    private static int Measure<T>(INatsSerialize<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return writer.WrittenCount;
    }
}
