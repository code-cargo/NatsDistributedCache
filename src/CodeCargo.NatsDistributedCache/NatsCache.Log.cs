using Microsoft.Extensions.Logging;

namespace CodeCargo.NatsDistributedCache;

public partial class NatsCache
{
    private void LogException(Exception exception) =>
        _logger.LogError(EventIds.Exception, exception, "An exception occurred in NATS KV store.");

    private void LogConnectionError(Exception exception) =>
        _logger.LogError(EventIds.ConnectionError, exception, "Error connecting to NATS KV store.");

    private void LogConnectionIssue() =>
        _logger.LogWarning(EventIds.ConnectionIssue, "Connection issue with NATS KV store.");

    private void LogConnected() =>
        _logger.LogInformation(EventIds.Connected, "Connected to NATS KV store.");

    private void LogUpdateFailed(string key) => _logger.LogDebug(
        EventIds.UpdateFailed,
        "Sliding expiration update failed for key {Key} due to optimistic concurrency control",
        key);

    private static class EventIds
    {
        public static readonly EventId ConnectionIssue = new(100, nameof(ConnectionIssue));
        public static readonly EventId ConnectionError = new(101, nameof(ConnectionError));
        public static readonly EventId Connected = new(102, nameof(Connected));
        public static readonly EventId UpdateFailed = new(103, nameof(UpdateFailed));
        public static readonly EventId Exception = new(104, nameof(Exception));
    }
}
