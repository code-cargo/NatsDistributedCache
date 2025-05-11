using System.Buffers;
using Microsoft.Extensions.Caching.Hybrid;
using Moq;
using NATS.Client.Core;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache.UnitTests.Serialization;

/// <summary>
/// Tests for the NatsHybridCacheSerializerFactory
/// </summary>
public class NatsHybridCacheSerializerFactoryTests : TestBase
{
    [Fact]
    public void TryCreateSerializer_String_CreatesSerializer()
    {
        // Arrange
        var serializerRegistry = NatsOpts.Default.SerializerRegistry;
        var factory = new NatsHybridCacheSerializerFactory(serializerRegistry);

        // Act
        var result = factory.TryCreateSerializer<string>(out var serializer);

        // Assert
        Assert.True(result);
        Assert.NotNull(serializer);

        // Test serialization and deserialization to ensure it works end-to-end
        const string testValue = "Hello, NatsHybridCacheSerializer!";
        var writer = new ArrayBufferWriter<byte>();

        // Serialize
        serializer.Serialize(testValue, writer);

        // Deserialize
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var deserializedValue = serializer.Deserialize(sequence);

        // Verify
        Assert.Equal(testValue, deserializedValue);
    }

    [Fact]
    public void TryCreateSerializer_UnsupportedType_ReturnsFalse()
    {
        // Arrange - Create a mock registry that throws exceptions when accessed
        var mockRegistry = new Mock<INatsSerializerRegistry>();
        mockRegistry.Setup(r => r.GetSerializer<It.IsAnyType>())
            .Throws<InvalidOperationException>();
        mockRegistry.Setup(r => r.GetDeserializer<It.IsAnyType>())
            .Throws<InvalidOperationException>();

        var factory = new NatsHybridCacheSerializerFactory(mockRegistry.Object);

        // Act
        var result = factory.TryCreateSerializer<string>(out var serializer);

        // Assert
        Assert.False(result);
        Assert.Null(serializer);

        // Verify the serializer was requested
        mockRegistry.Verify(r => r.GetSerializer<string>(), Times.Once);
    }
}
