using System.Text.RegularExpressions;

namespace CodeCargo.Nats.DistributedCache.UnitTests.KeyEncoder;

public partial class NatsCacheKvKeyEncoderTest
{
    private readonly NatsCacheKvKeyEncoder _encoder = new();

    [Theory]
    [InlineData("orders.pending")] // simple, already-legal ASCII
    [InlineData(".leading")] // leading dot
    [InlineData("trailing.")] // trailing dot
    [InlineData(".both.")] // leading + trailing dots
    [InlineData("naïve.café")] // Unicode with accented chars
    [InlineData("spaces and #/+/!")] // spaces + disallowed ASCII
    [InlineData("xn--already")] // segment that starts with "xn--"
    [InlineData("emoji😀key")] // emoji inside key
    [InlineData("prod.release-v1.*")] // wildcard asterisk (disallowed ASCII)
    public void EncodeDecode_RoundTrips_And_Yields_ValidKeys(string rawKey)
    {
        // ---- encode -----------------------------------------------------
        var encoded = _encoder.Encode(rawKey);

        // must match the KV character whitelist
        Assert.Matches(ValidKvKeyRegex(), encoded);

        // must not start / end with a dot
        Assert.False(encoded.StartsWith('.'), "Encoded key must not start with '.'");
        Assert.False(encoded.EndsWith('.'), "Encoded key must not end with '.'");

        // ---- decode -----------------------------------------------------
        var decoded = _encoder.Decode(encoded);

        Assert.Equal(rawKey, decoded); // round-trip check
    }

    [GeneratedRegex(@"^[-_=\.A-Za-z0-9]+$")]
    private static partial Regex ValidKvKeyRegex();
}
