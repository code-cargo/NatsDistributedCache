namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Shared, deterministic sample data for the serialization benchmarks and the size report.
/// </summary>
internal static class BenchmarkData
{
    /// <summary>
    /// Gets a fixed absolute-expiration instant. A constant is used so the benchmark is reproducible and
    /// does not depend on the wall clock.
    /// </summary>
    public static DateTimeOffset AbsoluteExpiration { get; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Gets the sliding-expiration window expressed in ticks.
    /// </summary>
    public static long SlidingExpirationTicks { get; } = TimeSpan.FromMinutes(10).Ticks;

    /// <summary>
    /// Builds a deterministic payload of the requested size.
    /// </summary>
    /// <param name="size">Payload size in bytes.</param>
    /// <returns>The payload bytes.</returns>
    public static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        return payload;
    }

    /// <summary>
    /// Builds a JSON envelope with a payload of the requested size.
    /// </summary>
    /// <param name="size">Payload size in bytes.</param>
    /// <returns>The populated <see cref="JsonCacheEntry"/>.</returns>
    public static JsonCacheEntry CreateJsonEntry(int size) => new()
    {
        AbsoluteExpiration = AbsoluteExpiration,
        SlidingExpirationTicks = SlidingExpirationTicks,
        Data = CreatePayload(size),
    };
}
