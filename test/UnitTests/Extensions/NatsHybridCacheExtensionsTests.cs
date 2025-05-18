using CodeCargo.Nats.HybridCache;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache.UnitTests.Extensions;

public class NatsHybridCacheExtensionsTests
{
    private readonly Mock<INatsConnection> _mockNatsConnection = new();

    [Fact]
    public void AddNatsHybridCache_RegistersSerializerFactoryAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);

        services.AddNatsHybridCache(options =>
        {
            options.BucketName = "cache";
        });

        var registration = services.FirstOrDefault(d => d.ServiceType == typeof(IHybridCacheSerializerFactory));
        Assert.NotNull(registration);
        Assert.Equal(ServiceLifetime.Singleton, registration!.Lifetime);
    }

    [Fact]
    public void AddNatsHybridCache_ReplacesPreviouslyUserRegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        services.AddSingleton<IHybridCacheSerializerFactory>(new FakeHybridCacheSerializerFactory());

        services.AddNatsHybridCache(options =>
        {
            options.BucketName = "cache";
        });

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHybridCacheSerializerFactory>();

        Assert.NotEqual(typeof(FakeHybridCacheSerializerFactory), factory.GetType());
        Assert.Contains("NatsHybridCacheSerializerFactory", factory.GetType().Name);
    }

    [Fact]
    public void AddNatsHybridCache_SetsCacheOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        const string expectedNamespace = "TestNamespace";

        services.AddNatsHybridCache(options =>
        {
            options.BucketName = "cache";
            options.CacheKeyPrefix = expectedNamespace;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NatsCacheOptions>>().Value;

        Assert.Equal(expectedNamespace, options.CacheKeyPrefix);
        Assert.Equal("cache", options.BucketName);
    }

    [Fact]
    public void AddNatsHybridCache_UsesCacheOptionsAction()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockNatsConnection.Object);
        var wasInvoked = false;

        services.AddNatsHybridCache(options =>
        {
            options.BucketName = "cache";
            wasInvoked = true;
        });

        var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<IHybridCacheSerializerFactory>();

        Assert.True(wasInvoked);
    }

    [Fact]
    public void AddNatsHybridCache_AcceptsConnectionServiceKey_Parameter()
    {
        var services = new ServiceCollection();
        var defaultConnection = new Mock<INatsConnection>().Object;
        var keyedConnection = new Mock<INatsConnection>().Object;

        services.AddSingleton(defaultConnection);
        services.AddKeyedSingleton("my-key", keyedConnection);

        services.AddNatsHybridCache(
            options => { options.BucketName = "cache"; },
            connectionServiceKey: "my-key");

        var cacheReg = services.FirstOrDefault(d => d.ServiceType == typeof(IHybridCacheSerializerFactory));
        Assert.NotNull(cacheReg);
        Assert.Equal(ServiceLifetime.Singleton, cacheReg!.Lifetime);
        Assert.Null(cacheReg.ImplementationType);
        Assert.NotNull(cacheReg.ImplementationFactory);

        var optionsReg = services.FirstOrDefault(d => d.ServiceType == typeof(IConfigureOptions<NatsCacheOptions>));
        Assert.NotNull(optionsReg);
    }

    [Fact]
    public void AddNatsHybridCacheSerializerFactory_AddsSerializerFactory()
    {
        var builderMock = new Mock<IHybridCacheBuilder>();
        var registry = NatsOpts.Default.SerializerRegistry;

        builderMock
            .Setup(b => b.AddSerializerFactory(It.IsAny<IHybridCacheSerializerFactory>()))
            .Returns(builderMock.Object)
            .Verifiable();

        var result = builderMock.Object.AddNatsHybridCacheSerializerFactory(registry);

        builderMock.Verify(b => b.AddSerializerFactory(It.IsAny<IHybridCacheSerializerFactory>()), Times.Once);
        Assert.Equal(builderMock.Object, result);
    }

    private class FakeHybridCacheSerializerFactory : IHybridCacheSerializerFactory
    {
        public bool TryCreateSerializer<T>(out IHybridCacheSerializer<T>? serializer)
        {
            serializer = null;
            return false;
        }
    }
}
