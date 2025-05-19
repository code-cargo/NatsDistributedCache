namespace CodeCargo.Nats.DistributedCache.IntegrationTests;

[CollectionDefinition(Name)]
public class NatsCollection : ICollectionFixture<NatsIntegrationFixture>
{
    public const string Name = nameof(NatsCollection);
}
