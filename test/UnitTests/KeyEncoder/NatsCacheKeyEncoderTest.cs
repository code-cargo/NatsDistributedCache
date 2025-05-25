using System.Text.RegularExpressions;

namespace CodeCargo.Nats.DistributedCache.UnitTests.KeyEncoder;

public partial class NatsCacheKeyEncoderTest
{
    private readonly NatsCacheKeyEncoder _encoder = new();

    [Theory]
    [InlineData("orders.pending", "orders.pending")] // simple, already-legal ASCII
    [InlineData(".leading", "=2Eleading")] // leading dot
    [InlineData("trailing.", "trailing=2E")] // trailing dot
    [InlineData(".both.", "=2Eboth=2E")] // leading + trailing dots
    [InlineData("naïve.café", "na=C3=AFve.caf=C3=A9")] // Unicode with accented chars
    [InlineData("spaces and #/+/!", "spaces=20and=20=23=2F=2B=2F=21")] // spaces + disallowed ASCII
    [InlineData("emoji😀key", "emoji=F0=9F=98=80key")] // emoji inside key
    [InlineData("prod.release-v1.*", "prod.release-v1.=2A")] // wildcard asterisk (disallowed ASCII)
    [InlineData("~tilde~", "=7Etilde=7E")] // tilde is encoded
    [InlineData("=equal=", "=3Dequal=3D")] // equal is encoded
    [InlineData("....", "=2E..=2E")] // only leading + trailing dots encoded
    public void EncodeDecode_RoundTrips_ValidKeys(string rawKey, string encodedKey)
    {
        // encode
        var encoded = _encoder.Encode(rawKey);

        // must match the KV character whitelist
        Assert.True(ValidEncodedKey(encoded), "Encoded key must contain only allowed characters");

        // must not start / end with a dot
        Assert.False(encoded.StartsWith('.'), "Encoded key must not start with '.'");
        Assert.False(encoded.EndsWith('.'), "Encoded key must not end with '.'");

        // check encoded
        Assert.Equal(encodedKey, encoded);

        // decode
        var decoded = _encoder.Decode(encoded);

        // check decoded
        Assert.Equal(rawKey, decoded);
    }

    private static bool ValidEncodedKey(string rawKey) =>
        !rawKey.StartsWith('.')
        && !rawKey.EndsWith('.')
        && ValidEncodedKeyRegex().IsMatch(rawKey);

    [GeneratedRegex("^[-_=.A-Za-z0-9]+$", RegexOptions.Compiled)]
    private static partial Regex ValidEncodedKeyRegex();
}
