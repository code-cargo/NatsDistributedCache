using System.Text.RegularExpressions;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// URL-encoding implementation that keeps already allowed keys
/// untouched and URL-encodes everything else. % characters in the
/// final encoded output are replaced with = characters to conform
/// to the NATS KV key rules.
/// </summary>
public sealed partial class NatsCacheKeyEncoder : INatsCacheKeyEncoder
{
    /// <inheritdoc />
    public string Encode(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            throw new ArgumentException("Key must not be null or empty.", nameof(raw));
        }

        if (ValidUnencodedKey(raw))
        {
            // already valid
            return raw;
        }

        var encoded = Uri.EscapeDataString(raw);
        encoded = encoded.Replace("~", "%7E");
        if (encoded.StartsWith('.'))
        {
            encoded = "%2E" + encoded[1..];
        }

        if (encoded.EndsWith('.'))
        {
            encoded = encoded[..^1] + "%2E";
        }

        encoded = encoded.Replace('%', '=');
        return encoded;
    }

    /// <inheritdoc />
    public string Decode(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key must not be null or empty.", nameof(key));
        }

        if (!key.Contains('='))
        {
            // nothing to decode
            return key;
        }

        var decoded = key.Replace('=', '%');
        return Uri.UnescapeDataString(decoded);
    }

    private static bool ValidUnencodedKey(string rawKey) =>
        !rawKey.StartsWith('.')
        && !rawKey.EndsWith('.')
        && ValidUnencodedKeyRegex().IsMatch(rawKey);

    // Regex pattern to match valid NATS KV keys with = removed, since =
    // is used instead of % to mark an encoded character sequence
    [GeneratedRegex("^[-_.A-Za-z0-9]+$", RegexOptions.Compiled)]
    private static partial Regex ValidUnencodedKeyRegex();
}
