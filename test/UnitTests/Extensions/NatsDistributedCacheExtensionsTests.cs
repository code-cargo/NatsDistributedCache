using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Extensions;

public class CacheServiceExtensionsUnitTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection = new();

    [Fact]
    public void AddNatsCache_RegistersDistributedCacheAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);

        // Act
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = "cache";
        });

        // Assert
        var distributedCache = services.FirstOrDefault(desc => desc.ServiceType == typeof(IDistributedCache));

        Assert.NotNull(distributedCache);
        Assert.Equal(ServiceLifetime.Singleton, distributedCache.Lifetime);
    }

    [Fact]
    public void AddNatsCache_ReplacesPreviouslyUserRegisteredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        services.AddSingleton<IDistributedCache>(new FakeDistributedCache());

        // Act
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = "cache";
        });

        // Build the provider to verify registrations
        var provider = services.BuildServiceProvider();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();

        // Assert
        Assert.NotEqual(typeof(FakeDistributedCache), distributedCache.GetType());
        Assert.Contains("NatsCache", distributedCache.GetType().Name);
    }

    [Fact]
    public void AddNatsCache_SetsCacheOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        var expectedNamespace = "TestNamespace";

        // Act
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = "cache";
            options.CacheKeyPrefix = expectedNamespace;
        });

        // Build the provider to verify options
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NatsCacheOptions>>().Value;

        // Assert
        Assert.Equal(expectedNamespace, options.CacheKeyPrefix);
        Assert.Equal("cache", options.BucketName);
    }

    [Fact]
    public void AddNatsCache_UsesCacheOptionsAction()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        var wasInvoked = false;

        // Act
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = "cache";
            wasInvoked = true;
        });

        // Build service provider and resolve options to trigger the setup action
        var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<IDistributedCache>();

        // Assert
        Assert.True(wasInvoked);
    }

    [Fact]
    public void AddNatsCache_AcceptsConnectionServiceKey_Parameter()
    {
        // Arrange
        var services = new ServiceCollection();
        var defaultConnection = new Mock<INatsConnection>().Object;
        var keyedConnection = new Mock<INatsConnection>().Object;

        services.AddSingleton(defaultConnection);
        services.AddKeyedSingleton("my-key", keyedConnection);

        // Act - ensure this doesn't throw an exception
        services.AddNatsDistributedCache(
            options => { options.BucketName = "cache"; },
            connectionServiceKey: "my-key");

        // Assert
        // Verify the IDistributedCache registration looks correct
        var cacheRegistration = services.FirstOrDefault(x => x.ServiceType == typeof(IDistributedCache));
        Assert.NotNull(cacheRegistration);
        Assert.Equal(ServiceLifetime.Singleton, cacheRegistration.Lifetime);
        Assert.Null(cacheRegistration.ImplementationType); // Should use a factory, not a direct type
        Assert.NotNull(cacheRegistration.ImplementationFactory); // Should use a factory registration

        // Verify the NatsCacheOptions were configured
        var optionsRegistration = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IConfigureOptions<NatsCacheOptions>));
        Assert.NotNull(optionsRegistration);
    }

    [Fact]
    public void AddNatsCache_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);

        var result = services.AddNatsDistributedCache(options => options.BucketName = "cache");

        Assert.Same(services, result);
    }

    [Fact]
    public void ToHybridCacheSerializerFactory_CreatesWorkingFactory()
    {
        var registry = NatsOpts.Default.SerializerRegistry;

        var factory = registry.ToHybridCacheSerializerFactory();

        Assert.NotNull(factory);
        var created = factory.TryCreateSerializer<string>(out var serializer);
        Assert.True(created);

        const string value = "hello";
        var writer = new ArrayBufferWriter<byte>();
        serializer!.Serialize(value, writer);
        var seq = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.Equal(value, serializer.Deserialize(seq));
    }

    private class FakeDistributedCache : IDistributedCache
    {
        public byte[] Get(string key) => throw new NotImplementedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            throw new NotImplementedException();

        public void Refresh(string key) => throw new NotImplementedException();

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotImplementedException();

        public void Remove(string key) => throw new NotImplementedException();

        public Task RemoveAsync(string key, CancellationToken token = default) => throw new NotImplementedException();

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new NotImplementedException();

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default) => throw new NotImplementedException();
    }
}
