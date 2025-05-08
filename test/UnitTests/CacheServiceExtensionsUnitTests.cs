using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;
using Xunit;

namespace CodeCargo.NatsDistributedCache.UnitTests;

public class CacheServiceExtensionsUnitTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection;

    public CacheServiceExtensionsUnitTests()
    {
        _mockNatsConnection = new Mock<INatsConnection>();
    }

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
        var expectedInstanceName = "TestInstance";

        // Act
        services.AddNatsDistributedCache(options =>
        {
            options.BucketName = "cache";
            options.InstanceName = expectedInstanceName;
        });

        // Build the provider to verify options
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NatsCacheOptions>>().Value;

        // Assert
        Assert.Equal(expectedInstanceName, options.InstanceName);
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

    private class FakeDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new NotImplementedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new NotImplementedException();

        public void Refresh(string key) => throw new NotImplementedException();

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotImplementedException();

        public void Remove(string key) => throw new NotImplementedException();

        public Task RemoveAsync(string key, CancellationToken token = default) => throw new NotImplementedException();

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotImplementedException();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new NotImplementedException();
    }
}
