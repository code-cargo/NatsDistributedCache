namespace NatsAppHost;

public class NatsResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string NatsEndpointName = "nats";

    // An EndpointReference is a core .NET Aspire type used for keeping
    // track of endpoint details in expressions. Simple literal values cannot
    // be used because endpoints are not known until containers are launched.
    private EndpointReference? _natsReference;

    public EndpointReference NatsEndpoint => _natsReference ??= new EndpointReference(this, NatsEndpointName);

    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create(
        $"nats://{NatsEndpoint.Property(EndpointProperty.Host)}:{NatsEndpoint.Property(EndpointProperty.Port)}");
}
