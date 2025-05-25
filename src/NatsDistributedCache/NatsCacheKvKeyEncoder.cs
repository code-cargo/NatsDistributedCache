using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeCargo.Nats.DistributedCache;

/// <summary>
/// Punycode-based implementation that:
/// • keeps any characters already allowed by the KV regex untouched
/// • segment-encodes the rest with RFC 3492 Punycode
/// </summary>
public sealed partial class NatsCacheKvKeyEncoder : INatsCacheKvKeyEncoder
{
    private readonly IdnMapping _idn = new(); // RFC 3492 encoder/decoder

    /// <inheritdoc />
    public string Encode(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            throw new ArgumentException("Key must not be null or empty.", nameof(raw));
        }

        // already legal → leave as-is
        if (AllowedKeyRegex().IsMatch(raw))
        {
            return raw;
        }

        var leadDot = raw[0] == '.';
        var trailDot = raw[^1] == '.';

        // strip dots so the remaining text can be processed segment-wise
        var working = leadDot ? raw[1..] : raw;
        working = trailDot && working.Length > 0 ? working[..^1] : working;

        // dot-separated segments are puny-encoded only when required
        var parts = working.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];

            // already legal and not a puny prefix → leave as-is
            if (AllowedSegmentRegex().IsMatch(seg) &&
                !seg.StartsWith("xn--", StringComparison.Ordinal))
            {
                continue;
            }

            // Map ASCII so that IdnMapping accepts all characters, then
            // encode with the built-in Punycode implementation. The "xn--"
            // prefix marks encoded segments.
            var mapped = MapAscii(seg);
            var ascii = _idn.GetAscii(mapped); // RFC 3492
            parts[i] = $"xn--{ascii}";
        }

        var joined = string.Join('.', parts);

        // sentinel for a leading dot
        if (leadDot)
        {
            joined = $"xn--.{joined}";
        }

        // sentinel for a trailing dot
        if (trailDot)
        {
            joined = $"{joined}.xn--";
        }

        return joined;
    }

    /// <inheritdoc />
    public string Decode(string kvKey)
    {
        if (string.IsNullOrEmpty(kvKey))
            throw new ArgumentException("Key must not be null or empty.", nameof(kvKey));

        var leadDot = false;
        var trailDot = false;

        // detect & strip edge-dot sentinels
        if (kvKey.StartsWith("xn--.", StringComparison.Ordinal))
        {
            leadDot = true;
            kvKey = kvKey[5..]; // drop "xn--."
        }

        if (kvKey.EndsWith(".xn--", StringComparison.Ordinal))
        {
            trailDot = true;
            kvKey = kvKey[..^5]; // drop ".xn--"
        }

        // per-segment Puny-decode where required
        var parts = kvKey.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (!seg.StartsWith("xn--", StringComparison.Ordinal))
            {
                continue;
            }

            var inner = seg[4..]; // drop the outer "xn--"
            var decoded = _idn.GetUnicode(inner); // RFC 3492 → UTF‑16
            decoded = UnmapAscii(decoded);
            parts[i] = decoded;
        }

        var result = string.Join('.', parts);
        if (leadDot)
        {
            result = $".{result}";
        }

        if (trailDot)
        {
            result = $"{result}.";
        }

        return result;
    }

    [GeneratedRegex("^[^.][-_=.A-Za-z0-9]+[^.]$", RegexOptions.Compiled)]
    private static partial Regex AllowedKeyRegex();

    [GeneratedRegex("^[-_=A-Za-z0-9]+$", RegexOptions.Compiled)]
    private static partial Regex AllowedSegmentRegex();

    private static string MapAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x80)
            {
                var mapped = rune.Value + 0x2800; // use Braille range for stability
                builder.Append(char.ConvertFromUtf32(mapped));
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    private static string UnmapAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x2800 and < 0x2880)
            {
                var unmapped = rune.Value - 0x2800;
                builder.Append(char.ConvertFromUtf32(unmapped));
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }
}
