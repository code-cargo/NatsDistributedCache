using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Net;

namespace CodeCargo.NatsDistributedCache.PerfTest;

/// <summary>
/// Performance test for NatsCache that tracks cache operations
/// </summary>
public class PerfTest
{
    // Configuration
    private const int MaxTestRuntimeSecs = 60;
    private const int NumCacheItems = 10_000;
    private const int FieldWidth = 12;
    private const int BatchSize = 100;
    private const int ValueDataSizeBytes = 256;
    private const int AbsoluteExpirationSecs = 10;
    private const int InsertDelayMs = 10;
    private const int RetrieveDelayMs = 5;
    private const int RemoveDelayMs = 50;
    private const int StatsUpdateIntervalMs = 500;

    // Dependencies
    private readonly INatsConnection _nats;
    private readonly NatsCache _cache;

    // Stats tracking
    private readonly Stopwatch _stopwatch = new();
    private readonly ConcurrentDictionary<string, long> _stats = new();
    private readonly List<string> _activeKeys = [];
    private readonly Random _random = new(42); // Fixed seed for reproducibility

    // Operation tracking via kvstore watcher
    private readonly SemaphoreSlim _watchLock = new(1, 1);
    private NatsKVStore? _kvStore;

    public PerfTest(INatsConnection nats)
    {
        _nats = nats;

        var options = Options.Create(new NatsCacheOptions
        {
            BucketName = "cache"
        });

        _cache = new NatsCache(options, NullLogger<NatsCache>.Instance, nats);

        // Initialize statistics counters
        _stats["KeysInserted"] = 0;
        _stats["KeysRetrieved"] = 0;
        _stats["KeysRemoved"] = 0;
        _stats["KeysExpired"] = 0;
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Starting CodeCargo NatsDistributedCache Performance Test - {DateTime.Now}");
        Console.WriteLine($"Testing with {NumCacheItems} cache items");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(MaxTestRuntimeSecs));

        // Create and connect the watcher to track operations
        await InitializeWatcher(cts.Token);

        // Start timer
        _stopwatch.Start();

        // Create tasks for the test operations
        var printTask = StartPrintTask(cts.Token);
        var watchTask = StartWatchTask(cts.Token);
        var insertTask = StartInsertTask(cts.Token);
        var retrieveTask = StartRetrieveTask(cts.Token);
        var removeTask = StartRemoveTask(cts.Token);

        try
        {
            // Wait for all tasks to complete or cancellation
            await Task.WhenAll(watchTask, insertTask, retrieveTask, removeTask);
        }
        catch (OperationCanceledException)
        {
            // Expected when test completes
        }
        finally
        {
            // Stop stopwatch
            _stopwatch.Stop();

            // Make sure print task completes
            await cts.CancelAsync();
            try
            {
                await printTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }

            // Print final stats
            PrintFinalStats();
        }
    }

    private static string FormatElapsedTime(TimeSpan elapsed) =>
        $"{Math.Floor(elapsed.TotalMinutes):00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}";

    private byte[] GenerateRandomData(int size)
    {
        var data = new byte[size];
        _random.NextBytes(data);
        return data;
    }

    private async Task InitializeWatcher(CancellationToken ct)
    {
        await _watchLock.WaitAsync(ct);
        try
        {
            if (_kvStore == null)
            {
                var jsContext = _nats.CreateJetStreamContext();
                var kvContext = new NatsKVContext(jsContext);
                _kvStore = (NatsKVStore)await kvContext.GetStoreAsync("cache", ct);
            }
        }
        finally
        {
            _watchLock.Release();
        }
    }

    private Task StartInsertTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    for (var i = 0; i < NumCacheItems && !ct.IsCancellationRequested; i++)
                    {
                        // Create a batch of items
                        for (var j = 0; j < BatchSize && i + j < NumCacheItems && !ct.IsCancellationRequested; j++)
                        {
                            var key = $"BatchNum{i}IndividualNum{j}";
                            var data = GenerateRandomData(ValueDataSizeBytes); // 256 byte values

                            try
                            {
                                var options = new DistributedCacheEntryOptions
                                {
                                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(AbsoluteExpirationSecs),
                                };

                                await _cache.SetAsync(key, data, options, ct);

                                lock (_activeKeys)
                                {
                                    _activeKeys.Add(key);
                                }

                                // Note: actual count is tracked by the watcher
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Console.WriteLine($"Insert error: {ex.Message}");
                            }
                        }

                        // Small delay between batches
                        await Task.Delay(InsertDelayMs, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
            },
            ct);

    private Task StartRetrieveTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    // Wait a bit before starting retrieval operations
                    await Task.Delay(1000, ct);

                    while (!ct.IsCancellationRequested)
                    {
                        await ProcessSingleRetrievalAsync(ct);
                        await Task.Delay(RetrieveDelayMs, ct); // Small delay between retrievals
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
            },
            ct);

    private async Task ProcessSingleRetrievalAsync(CancellationToken ct)
    {
        var (key, index) = GetRandomActiveKey();
        if (key == null)
            return;

        try
        {
            var result = await _cache.GetAsync(key, ct);
            _stats.AddOrUpdate("KeysRetrieved", 1, (_, count) => count + 1);

            if (result == null)
            {
                _stats.AddOrUpdate("KeysExpired", 1, (_, count) => count + 1);
                RemoveExpiredKeyFromActiveList(index);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Retrieve error: {ex.Message}");
        }
    }

    private (string? Key, int Index) GetRandomActiveKey()
    {
        lock (_activeKeys)
        {
            if (_activeKeys.Count == 0)
                return (null, -1);

            var index = _random.Next(_activeKeys.Count);
            return (_activeKeys[index], index);
        }
    }

    private void RemoveExpiredKeyFromActiveList(int index)
    {
        lock (_activeKeys)
        {
            if (index >= 0 && index < _activeKeys.Count)
            {
                _activeKeys.RemoveAt(index);
            }
        }
    }

    private Task StartRemoveTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    // Wait a bit before starting expiry operations
                    await Task.Delay(2000, ct);

                    while (!ct.IsCancellationRequested)
                    {
                        string? key = null;

                        lock (_activeKeys)
                        {
                            if (_activeKeys.Count > 0)
                            {
                                var index = _random.Next(_activeKeys.Count);
                                key = _activeKeys[index];
                                _activeKeys.RemoveAt(index);
                            }
                        }

                        if (key != null)
                        {
                            try
                            {
                                await _cache.RemoveAsync(key, ct);

                                // Note: actual count is tracked by the watcher
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Console.WriteLine($"Remove error: {ex.Message}");
                            }
                        }

                        await Task.Delay(RemoveDelayMs, ct); // Less frequent expiry operations
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
            },
            ct);

    private Task StartWatchTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    if (_kvStore == null)
                    {
                        Console.WriteLine("KV Store not initialized");
                        return;
                    }

                    var opsBuffer = new List<NatsKVOperation>();
                    var statsUpdateInterval = TimeSpan.FromMilliseconds(StatsUpdateIntervalMs);
                    var lastStatsUpdate = DateTimeOffset.Now;

                    await foreach (var entry in _kvStore.WatchAsync<ReadOnlySequence<byte>>(
                                       ">",
                                       opts: new NatsKVWatchOpts { MetaOnly = true },
                                       cancellationToken: ct))
                    {
                        opsBuffer.Add(entry.Operation);
                        if (DateTimeOffset.Now - lastStatsUpdate <= statsUpdateInterval)
                        {
                            await UpdateStatsIfNeeded(opsBuffer);
                            lastStatsUpdate = DateTimeOffset.Now;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Watch error: {ex.Message}");
                }
            },
            ct);

    private Task UpdateStatsIfNeeded(List<NatsKVOperation> opsBuffer)
    {
        // Process the buffer and update stats
        // Read operations are tracked directly in StartRetrieveTask
        var puts = opsBuffer.Count(op => op == NatsKVOperation.Put);
        var deletes = opsBuffer.Count(op => op == NatsKVOperation.Del);

        if (puts > 0)
        {
            _stats.AddOrUpdate("KeysInserted", puts, (_, count) => count + puts);
        }

        if (deletes > 0)
        {
            _stats.AddOrUpdate("KeysRemoved", deletes, (_, count) => count + deletes);
        }

        opsBuffer.Clear();
        return Task.CompletedTask;
    }

    private Task StartPrintTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Clear console
                        Console.Clear();

                        // Print current statistics
                        PrintProgress();

                        // Wait before printing again
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
            },
            ct);

    private void PrintProgress()
    {
        var totalOps = _stats["KeysInserted"] + _stats["KeysRetrieved"] + _stats["KeysRemoved"];
        var opsPerSecond = (long)(totalOps / _stopwatch.Elapsed.TotalSeconds);
        Console.WriteLine("========== CodeCargo NatsDistributedCache Performance ==========");
        Console.WriteLine($"Current Keys:     {_activeKeys.Count,FieldWidth:N0}");
        Console.WriteLine($"Keys Inserted:    {_stats["KeysInserted"],FieldWidth:N0}");
        Console.WriteLine($"Keys Retrieved:   {_stats["KeysRetrieved"],FieldWidth:N0}");
        Console.WriteLine($"Keys Removed:     {_stats["KeysRemoved"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Cache Hits:       {_stats["KeysRetrieved"] - _stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine($"Cache Misses:     {_stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Elapsed Time:     {FormatElapsedTime(_stopwatch.Elapsed),FieldWidth}");
        Console.WriteLine($"Time Remaining:   {FormatElapsedTime(TimeSpan.FromSeconds(MaxTestRuntimeSecs) - _stopwatch.Elapsed),FieldWidth}");
        Console.WriteLine($"Total operations: {totalOps,FieldWidth:N0}");
        Console.WriteLine($"Ops per Second:   {opsPerSecond,FieldWidth:N0}");
        Console.WriteLine("==========================================================");
    }

    private void PrintFinalStats()
    {
        var totalOps = _stats["KeysInserted"] + _stats["KeysRetrieved"] + _stats["KeysRemoved"];
        var opsPerSecond = (long)(totalOps / _stopwatch.Elapsed.TotalSeconds);
        Console.Clear();
        Console.WriteLine("========== CodeCargo NatsDistributedCache Test Summary ==========");
        Console.WriteLine($"Test completed at:    {DateTime.Now}");
        Console.WriteLine($"Total test duration:  {FormatElapsedTime(_stopwatch.Elapsed)}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Final Current Keys:   {_activeKeys.Count,FieldWidth:N0}");
        Console.WriteLine($"Total Keys Inserted:  {_stats["KeysInserted"],FieldWidth:N0}");
        Console.WriteLine($"Total Keys Retrieved: {_stats["KeysRetrieved"],FieldWidth:N0}");
        Console.WriteLine($"Total Keys Removed:   {_stats["KeysRemoved"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Cache Hits:       {_stats["KeysRetrieved"] - _stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine($"Cache Misses:     {_stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Total operations:     {totalOps,FieldWidth:N0}");
        Console.WriteLine($"Average Ops/Second:   {opsPerSecond,FieldWidth:N0}");
        Console.WriteLine("==========================================================");

        // Also log memory usage
        var meg = Math.Pow(2, 20);
        var memoryMiB = Process.GetCurrentProcess().PrivateMemorySize64 / meg;
        var allocMiB = GC.GetTotalAllocatedBytes() / meg;
        Console.WriteLine($"Memory Usage MiB: {memoryMiB,FieldWidth:N0}");
        Console.WriteLine($"Total Alloc  MiB: {allocMiB,FieldWidth:N0}");
    }
}
