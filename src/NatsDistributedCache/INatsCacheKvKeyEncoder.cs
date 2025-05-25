namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Encodes / decodes arbitrary strings so they satisfy the NATS KV key rules.
/// </summary>
public interface INatsCacheKvKeyEncoder
{
    /// <summary>
    /// Encodes a raw string into a KV-legal key
    /// </summary>
    /// <param name="raw">The raw string to encode</param>
    /// <returns>A KV-legal key</returns>
    string Encode(string raw);

    /// <summary>
    /// Decodes a KV-legal key back to its original string
    /// </summary>
    /// <param name="kvKey">The encoded KV key</param>
    /// <returns>The original string</returns>
    string Decode(string kvKey);
}
