using System.Diagnostics;
using CodeCargo.Nats.DistributedCache.PerfTest.Utils;
using Microsoft.Extensions.Caching.Distributed;

namespace CodeCargo.Nats.DistributedCache.PerfTest;

public class PerfTest
{
    private const int NumKeys = 20_000;
    private const int ValueSizeBytes = 128;
    private static readonly int ParallelTasks = Environment.ProcessorCount;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDistributedCache _cache;
    private readonly string[] _keys;
    private readonly byte[] _valuePayload;
    private readonly List<Stage> _stages = [];
    private string _backendName = string.Empty;

    public PerfTest(IDistributedCache cache)
    {
        _cache = cache;

        // Pre-generate unique keys
        _keys = new string[NumKeys];
        for (var i = 0; i < NumKeys; i++)
        {
            _keys[i] = i.ToString();
        }

        // Prepare a sample value payload filled with the character '0'
        _valuePayload = new byte[ValueSizeBytes];
        Array.Fill(_valuePayload, (byte)'0'); // Fill with ASCII character '0' (value 48)
    }

    public async Task Run(string backendName, CancellationToken ct)
    {
        // Clear stages for a new run
        _stages.Clear();
        _backendName = backendName;

        // Run all stages sequentially
        await RunStage("Insert", SetWithAbsoluteExpiry, ct);
        await RunStage("Get", GetOperation, ct);
        await RunStage("Update", SetWithSlidingExpiry, ct);
        await RunStage("Get (refresh)", GetOperation, ct);
        await RunStage("Delete", DeleteOperation, ct);

        // Final display with results
        Console.Clear();
        PrintResults();
    }

    private async Task<TimeSpan> SetWithAbsoluteExpiry(string key, CancellationToken ct)
    {
        using var sw = StopwatchPool.Rent();
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) };
        await _cache.SetAsync(key, _valuePayload, options, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task<TimeSpan> GetOperation(string key, CancellationToken ct)
    {
        using var sw = StopwatchPool.Rent();
        await _cache.GetAsync(key, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task<TimeSpan> SetWithSlidingExpiry(string key, CancellationToken ct)
    {
        using var sw = StopwatchPool.Rent();
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) };
        await _cache.SetAsync(key, _valuePayload, options, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task<TimeSpan> DeleteOperation(string key, CancellationToken ct)
    {
        using var sw = StopwatchPool.Rent();
        await _cache.RemoveAsync(key, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    // Core method to run a batch of operations in parallel and collect metrics
    private async Task RunStage(
        string stageName,
        Func<string, CancellationToken, Task<TimeSpan>> operationFunc,
        CancellationToken ct)
    {
        // Create a new stage and add it to the collection
        var stage = new Stage(stageName);
        _stages.Add(stage);

        var totalOps = NumKeys;
        var completedOps = 0;

        // Start stage timing
        stage.StartTiming();

        // Start a background progress updater task
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.Run(
            async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var done = Volatile.Read(ref completedOps);
                    var percent = (done * 100.0) / totalOps;

                    // Get a snapshot of the current progress state
                    var progressLine = $"{stageName}: {done,8:N0}/{totalOps:N0} completed ({percent,6:F2}%)";

                    // First calculate all needed values, then clear and print
                    Console.Clear();

                    // Print the current results table
                    PrintResults();

                    // Print progress indicator below table with a newline
                    Console.WriteLine();
                    Console.WriteLine(progressLine);

                    if (done >= totalOps)
                        break;

                    await Task.Delay(ProgressUpdateInterval, cts.Token).ConfigureAwait(false);
                }
            },
            cts.Token);

        // Launch parallel worker tasks
        var tasks = new Task[ParallelTasks];
        var nextKeyIndex = 0;
        for (var t = 0; t < ParallelTasks; t++)
        {
            // Determine the range of keys this task will handle
            var chunkSize = totalOps / ParallelTasks;
            if (t < totalOps % ParallelTasks)
            {
                // distribute remainder keys one per task (for first few tasks)
                chunkSize++;
            }

            var startIndex = nextKeyIndex;
            var endIndex = startIndex + chunkSize;
            nextKeyIndex = endIndex;

            tasks[t] = Task.Run(
                async () =>
                {
                    for (var i = startIndex; i < endIndex && !ct.IsCancellationRequested; i++)
                    {
                        // Perform the operation and record its duration
                        var elapsed = await operationFunc(_keys[i], ct).ConfigureAwait(false);

                        // Add the operation duration to the stage
                        stage.AddOperationDuration(elapsed);

                        // Atomically increment the completed count for progress tracking
                        Interlocked.Increment(ref completedOps);
                    }
                },
                ct);
        }

        // Wait for all tasks to finish
        await Task.WhenAll(tasks);
        stage.StopTiming();

        // Ensure progress task exits and wait for it
        // Mark all ops completed for progress loop, in case it hasn't updated the final state yet
        Volatile.Write(ref completedOps, totalOps);
        await cts.CancelAsync();
        try
        {
            await progressTask;
        }
        catch (OperationCanceledException)
        {
            // ignore OperationCanceledException
        }
    }

    // Print the current results table
    private void PrintResults(bool clearScreen = false)
    {
        if (clearScreen)
        {
            Console.Clear();
        }

        // Define table width constants
        const int stageWidth = 15;
        const int opsWidth = 12;
        const int dataWidth = 10;
        const int durationWidth = 12;
        const int p50Width = 10;
        const int p95Width = 10;
        const int p99Width = 10;
        const int totalWidth = stageWidth + 1 + opsWidth + 1 + dataWidth + 1 + durationWidth + 1 + p50Width + 1 +
                               p95Width + 1 + p99Width;

        // Print backend information
        if (!string.IsNullOrEmpty(_backendName))
        {
            Console.WriteLine($"Backend: {_backendName}");
        }

        // Print header
        Console.WriteLine(
            "{0,-" + stageWidth + "} {1," + opsWidth + "} {2," + dataWidth + "} {3," + durationWidth + "} {4," +
            p50Width + "} {5," + p95Width + "} {6," + p99Width + "}",
            "Stage",
            "Operations",
            "Data (MiB)",
            "Duration (s)",
            "P50 (ms)",
            "P95 (ms)",
            "P99 (ms)");
        Console.WriteLine(new string('-', totalWidth));

        // Print each stage result in aligned columns
        long totalOperations = 0;
        double totalDuration = 0;
        double totalDataMiB = 0;

        foreach (var stage in _stages)
        {
            var operations = stage.Operations;
            totalOperations += operations;
            totalDuration += stage.Duration.TotalSeconds;

            // Calculate data size in MiB
            var dataMiB = (operations * (long)ValueSizeBytes) / (1024.0 * 1024.0);
            totalDataMiB += dataMiB;

            Console.WriteLine(
                "{0,-" + stageWidth + "} {1," + opsWidth + ":N0} {2," + dataWidth + ":F2} {3," + durationWidth +
                ":F2} {4," + p50Width + ":F2} {5," + p95Width + ":F2} {6," + p99Width + ":F2}",
                stage.Name,
                operations,
                dataMiB,
                stage.Duration.TotalSeconds,
                stage.GetPercentile(50),
                stage.GetPercentile(95),
                stage.GetPercentile(99));
        }

        // Add totals row if we have results
        if (_stages.Count > 0)
        {
            // Print totals row
            Console.WriteLine(new string('-', totalWidth));
            Console.WriteLine(
                "{0,-" + stageWidth + "} {1," + opsWidth + ":N0} {2," + dataWidth + ":F2} {3," + durationWidth +
                ":F2} {4," + p50Width + "} {5," + p95Width + "} {6," + p99Width + "}",
                "Total",
                totalOperations,
                totalDataMiB,
                totalDuration,
                string.Empty,
                string.Empty,
                string.Empty);
            Console.WriteLine(new string('-', totalWidth));
        }
    }

    /// <summary>
    /// Represents a stage in the performance test with real-time statistics
    /// </summary>
    private sealed class Stage
    {
        private readonly Stopwatch _duration = new();
        private readonly SortedSet<long> _sortedTicks = new();
        private readonly object _syncLock = new();
        private int _totalOps = 0;

        public Stage(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the name of this stage
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the number of operations completed so far
        /// </summary>
        public int Operations => _totalOps;

        /// <summary>
        /// Gets the total duration of this stage
        /// </summary>
        public TimeSpan Duration => _duration.Elapsed;

        /// <summary>
        /// Starts timing this stage
        /// </summary>
        public void StartTiming() => _duration.Start();

        /// <summary>
        /// Stops timing this stage
        /// </summary>
        public void StopTiming() => _duration.Stop();

        /// <summary>
        /// Adds a single operation duration to the statistics
        /// </summary>
        public void AddOperationDuration(TimeSpan duration)
        {
            // Store ticks instead of TimeSpan to reduce memory overhead
            var ticks = duration.Ticks;

            lock (_syncLock)
            {
                _sortedTicks.Add(ticks);
                _totalOps++;
            }
        }

        /// <summary>
        /// Gets the requested percentile of operation durations in milliseconds
        /// </summary>
        public double GetPercentile(double percentile)
        {
            lock (_syncLock)
            {
                // If we have no operations yet, return 0
                if (_sortedTicks.Count == 0)
                    return 0;

                // Calculate the index for the requested percentile
                var idx = (int)Math.Ceiling(percentile / 100.0 * _sortedTicks.Count) - 1;
                idx = Math.Max(0, Math.Min(idx, _sortedTicks.Count - 1));

                // Get the value at the specified index
                return new TimeSpan(_sortedTicks.ElementAt(idx)).TotalMilliseconds;
            }
        }
    }
}
