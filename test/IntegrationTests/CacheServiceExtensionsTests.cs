using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

[Collection(NatsCollection.Name)]
public class CacheServiceExtensionsTests : TestBase
{
    private readonly NatsIntegrationFixture _fixture;

    public CacheServiceExtensionsTests(NatsIntegrationFixture fixture)
        : base(fixture)
    {
        _fixture = fixture;
    }

    // All tests moved to UnitTests/CacheServiceExtensionsUnitTests.cs
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
