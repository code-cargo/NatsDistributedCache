using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.Hosting;

namespace CodeCargo.NatsDistributedCache.TestUtils;

public static class NatsTestExtensions
{
    public static IServiceCollection AddNatsTestClient(this IServiceCollection services, string natsConnectionString) =>
        services.AddNats(configureOpts: options =>
            options with
            {
                Url = natsConnectionString,
                RequestReplyMode = NatsRequestReplyMode.Direct,
            });
}
