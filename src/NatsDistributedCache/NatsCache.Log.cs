using Microsoft.Extensions.Logging;

namespace CodeCargo.NatsDistributedCache;

public partial class NatsCache
{
    private void LogException(Exception exception) =>
        _logger.LogError(EventIds.Exception, exception, "Exception in NatsDistributedCache");

    private void LogConnected(string bucketName) =>
        _logger.LogInformation(EventIds.Connected, "Connected to NATS KV bucket {bucketName}", bucketName);

    private void LogUpdateFailed(string key) => _logger.LogDebug(
        EventIds.UpdateFailed,
        "Sliding expiration update failed for key {Key} due to optimistic concurrency control",
        key);

    private static class EventIds
    {
        public static readonly EventId Connected = new(100, nameof(Connected));
        public static readonly EventId UpdateFailed = new(101, nameof(UpdateFailed));
        public static readonly EventId Exception = new(102, nameof(Exception));
    }
}
