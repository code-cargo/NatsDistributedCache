using Microsoft.Extensions.Logging;

namespace CodeCargo.Nats.DistributedCache;

public partial class NatsCache
{
    private void LogConnected(string bucketName) =>
        _logger.LogInformation(EventIds.Connected, "Connected to NATS KV bucket {bucketName}", bucketName);

    private void LogException(Exception exception) =>
        _logger.LogError(EventIds.Exception, exception, "Exception in NatsDistributedCache");

    private void LogSwallowedException(Exception exception) =>
        _logger.LogDebug(EventIds.Exception, exception, "Swallowed exception in NatsDistributedCache while reading into buffer");

    private static class EventIds
    {
        public static readonly EventId Connected = new(100, nameof(Connected));
        public static readonly EventId Exception = new(101, nameof(Exception));
    }
}
