using Moq;
using NATS.Client.KeyValueStore;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Extensions;

/// <summary>
/// Verifies the source-generated [GeneratedRegex] key validator in <see cref="NatsExtensions"/>
/// accepts and rejects exactly the same keys as the previous RegexOptions.Compiled implementation.
/// TryValidateKey is private, so validation is exercised through the public TryPutWithTtlAsync entry
/// point: a valid key passes validation and reaches the store, while an invalid key short-circuits
/// with a NatsKVException before the store is touched.
/// </summary>
public class NatsExtensionsKeyValidationTests
{
    [Theory]
    [InlineData("abc123")]
    [InlineData("a-b_c.d/e=f")] // full character set: - _ . / = plus letters and digits
    [InlineData("UPPER-lower-0-9")]
    [InlineData("path/to/key")]
    [InlineData("has.dots.inside")] // internal dots are allowed
    public async Task TryPutWithTtlAsync_ValidKey_PassesValidationAndReachesStore(string key)
    {
        // A valid key must pass validation and proceed to the store. The mocked store throws a
        // sentinel the moment its JetStreamContext is read (the first store member the extension
        // touches), so observing that exact sentinel proves validation accepted the key.
        var sentinel = new InvalidOperationException("reached-store");
        var store = new Mock<INatsKVStore>();
        store.SetupGet(s => s.JetStreamContext).Throws(sentinel);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Object.TryPutWithTtlAsync(key, "value"));

        Assert.Same(sentinel, thrown);
    }

    [Theory]
    [InlineData("", "Key cannot be empty")]
    [InlineData(" ", "Key cannot be empty")]
    [InlineData(".lead", "Key cannot start or end with a period")]
    [InlineData("trail.", "Key cannot start or end with a period")]
    [InlineData(".", "Key cannot start or end with a period")]
    [InlineData("has space", "Key contains invalid characters")]
    [InlineData("bad!char", "Key contains invalid characters")]
    [InlineData("with@sign", "Key contains invalid characters")]
    public async Task TryPutWithTtlAsync_InvalidKey_ReturnsErrorWithoutTouchingStore(
        string key,
        string expectedMessage)
    {
        // MockBehavior.Strict fails the test on any store access, proving invalid keys are rejected
        // before the extension touches the store.
        var store = new Mock<INatsKVStore>(MockBehavior.Strict);

        var result = await store.Object.TryPutWithTtlAsync(key, "value");

        Assert.False(result.Success);
        Assert.IsType<NatsKVException>(result.Error);
        Assert.Equal(expectedMessage, result.Error.Message);
    }
}
