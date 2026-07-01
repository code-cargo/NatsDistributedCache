using System.Buffers;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Prints the serialized envelope size (in bytes) of the JSON format for each payload size, so the
/// stored size can be inspected without running the full timing benchmarks. Issue #37 extends this
/// with the binary format for a side-by-side comparison.
/// </summary>
internal static class SizeReport
{
    private static readonly int[] PayloadSizes = [128, 1024, 8192, 65536, 262144];

    /// <summary>
    /// Writes the size table to the console.
    /// </summary>
    public static void Print()
    {
        var jsonSerializer = new NatsJsonContextSerializer<JsonCacheEntry>(BenchmarkJsonContext.Default);

        Console.WriteLine("Stored envelope size (bytes) — absolute + sliding expiration set");
        Console.WriteLine($"{"Payload",10} {"JSON",10}");
        Console.WriteLine(new string('-', 22));

        foreach (var size in PayloadSizes)
        {
            var jsonSize = Measure(jsonSerializer, BenchmarkData.CreateJsonEntry(size));
            Console.WriteLine($"{size,10} {jsonSize,10}");
        }
    }

    private static int Measure<T>(INatsSerialize<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return writer.WrittenCount;
    }
}
