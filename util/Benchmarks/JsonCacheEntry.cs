using System.Text.Json.Serialization;

namespace CodeCargo.Nats.DistributedCache.Benchmarks;

/// <summary>
/// Mirror of the legacy JSON cache envelope — property names and types identical to the pre-#37
/// <c>CacheEntry</c>. It lives in the benchmark project so the JSON baseline stays frozen regardless of
/// later changes to the production type.
/// </summary>
public sealed class JsonCacheEntry
{
    [JsonPropertyName("absexp")]
    public DateTimeOffset? AbsoluteExpiration { get; set; }

    [JsonPropertyName("sldexp")]
    public long? SlidingExpirationTicks { get; set; }

    [JsonPropertyName("data")]
    public byte[]? Data { get; set; }
}

/// <summary>
/// Source-generated JSON context for <see cref="JsonCacheEntry"/>, matching the production serializer setup.
/// </summary>
[JsonSerializable(typeof(JsonCacheEntry))]
public partial class BenchmarkJsonContext : JsonSerializerContext
{
}
