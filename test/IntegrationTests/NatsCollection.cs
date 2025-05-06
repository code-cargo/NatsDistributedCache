using Xunit;

namespace CodeCargo.NatsDistributedCache.IntegrationTests;

[CollectionDefinition(Name)]
public class NatsCollection : ICollectionFixture<NatsIntegrationFixture>
{
    public const string Name = nameof(NatsCollection);
}
