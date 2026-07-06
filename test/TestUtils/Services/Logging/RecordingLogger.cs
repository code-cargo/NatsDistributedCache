using Microsoft.Extensions.Logging;

namespace CodeCargo.Nats.DistributedCache.TestUtils.Services.Logging;

/// <summary>
/// A captured log entry recorded by <see cref="RecordingLogger{T}" />
/// </summary>
/// <param name="LogLevel">The level the entry was logged at</param>
/// <param name="EventId">The event id associated with the entry</param>
/// <param name="Exception">The exception attached to the entry, if any</param>
/// <param name="Message">The formatted log message</param>
public sealed record LogRecord(LogLevel LogLevel, EventId EventId, Exception? Exception, string Message);

/// <summary>
/// An <see cref="ILogger{T}" /> that captures log entries in memory so tests can assert on them
/// </summary>
/// <typeparam name="T">The category type</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogRecord> _records = new();

    /// <summary>
    /// Gets a snapshot of the entries captured so far
    /// </summary>
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records)
            {
                return _records.ToArray();
            }
        }
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var record = new LogRecord(logLevel, eventId, exception, formatter(state, exception));
        lock (_records)
        {
            _records.Add(record);
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;
}
