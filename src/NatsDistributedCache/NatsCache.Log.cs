using Microsoft.Extensions.Logging;

namespace CodeCargo.Nats.DistributedCache;

public partial class NatsCache
{
    private void LogConnected(string bucketName) =>
        _logger.LogInformation(EventIds.Connected, "Connected to NATS KV bucket {bucketName}", bucketName);

    private void LogException(Exception exception) =>
        _logger.LogError(EventIds.Exception, exception, "Exception in NatsDistributedCache");

    private void LogSwallowedException(Exception exception) =>
        _logger.LogWarning(EventIds.Exception, exception, "NATS cache read failed in TryGetAsync; returning a cache miss");

    private void LogUndeserializableEntry(string key) =>
        _logger.LogDebug(
            EventIds.UndeserializableEntry,
            "Cache entry for key {key} could not be deserialized (legacy or corrupt format); returning a cache miss",
            key);

    private static class EventIds
    {
        public static readonly EventId Connected = new(100, nameof(Connected));
        public static readonly EventId Exception = new(101, nameof(Exception));
        public static readonly EventId UndeserializableEntry = new(102, nameof(UndeserializableEntry));
    }
}
