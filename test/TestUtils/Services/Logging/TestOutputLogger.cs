using Microsoft.Extensions.Logging;
using Xunit;

namespace CodeCargo.NatsDistributedCache.TestUtils.Services.Logging;

/// <summary>
/// Extension methods for configuring logging in test context
/// </summary>
public static class TestLoggingExtensions
{
    /// <summary>
    /// Adds logging to xUnit test output
    /// </summary>
    /// <param name="builder">The logging builder</param>
    /// <param name="testOutputHelper">The test output helper</param>
    /// <returns>The logging builder for chaining</returns>
    public static ILoggingBuilder AddXUnitTestOutput(this ILoggingBuilder builder, ITestOutputHelper testOutputHelper)
    {
        builder.AddProvider(new TestOutputLoggerProvider(testOutputHelper));
        return builder;
    }
}

/// <summary>
/// A logger provider that writes logs to the test output
/// </summary>
public class TestOutputLoggerProvider(ITestOutputHelper testOutputHelper) : ILoggerProvider
{
    private readonly ITestOutputHelper _testOutputHelper = testOutputHelper ?? throw new ArgumentNullException(nameof(testOutputHelper));

    public ILogger CreateLogger(string categoryName) =>
        new TestOutputLogger(_testOutputHelper, categoryName);

    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Implementation of ILogger that writes to XUnit test output
/// </summary>
public class TestOutputLogger(ITestOutputHelper testOutputHelper, string categoryName) : ILogger
{
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            testOutputHelper.WriteLine($"[{logLevel}] {categoryName}: {formatter(state, exception)}");

            if (exception != null)
            {
                testOutputHelper.WriteLine($"Exception: {exception}");
            }
        }
        catch (InvalidOperationException)
        {
            // This can happen if the test has already completed
            // Just swallow this exception
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;
}

/// <summary>
/// Generic version of TestOutputLogger
/// </summary>
/// <typeparam name="T">The category type</typeparam>
public class TestOutputLogger<T>(ITestOutputHelper testOutputHelper)
    : TestOutputLogger(testOutputHelper, typeof(T).Name), ILogger<T>;
