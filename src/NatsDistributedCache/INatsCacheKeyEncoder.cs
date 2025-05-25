namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Encodes raw strings so they satisfy the NATS KV key rules.
/// </summary>
public interface INatsCacheKeyEncoder
{
    /// <summary>
    /// Encodes a raw string into a KV-legal key
    /// </summary>
    /// <param name="raw">The raw string to encode</param>
    /// <returns>A KV-legal key</returns>
    string Encode(string raw);
}
