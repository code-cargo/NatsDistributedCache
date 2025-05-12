using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
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
    private const int NumCacheItems = 10_000;
    private const int KeyExpirySecs = 5;
    private const int MaxTestRuntimeSecs = 60;
    private const int FieldWidth = 12;
    private const int BatchSize = 100;

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
        _stats["KeysExpired"] = 0;
        _stats["OperationsPerSec"] = 0;
        _stats["CurrentKeys"] = 0;
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
        var expireTask = StartExpireTask(cts.Token);

        try
        {
            // Wait for all tasks to complete or cancellation
            await Task.WhenAll(watchTask, insertTask, retrieveTask, expireTask);
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
                            var data = GenerateRandomData(256); // 256 byte values

                            try
                            {
                                var options = new DistributedCacheEntryOptions
                                {
                                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(KeyExpirySecs + _random.Next(KeyExpirySecs))
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
                        await Task.Delay(10, ct);
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
                        string? key = null;

                        lock (_activeKeys)
                        {
                            if (_activeKeys.Count > 0)
                            {
                                var index = _random.Next(_activeKeys.Count);
                                key = _activeKeys[index];
                            }
                        }

                        if (key != null)
                        {
                            try
                            {
                                await _cache.GetAsync(key, ct);

                                // Note: actual count is tracked by the watcher
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Console.WriteLine($"Retrieve error: {ex.Message}");
                            }
                        }

                        await Task.Delay(5, ct); // Small delay between retrievals
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
            },
            ct);

    private Task StartExpireTask(CancellationToken ct) =>
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
                                Console.WriteLine($"Expire error: {ex.Message}");
                            }
                        }

                        await Task.Delay(50, ct); // Less frequent expiry operations
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
                    var statsUpdateInterval = TimeSpan.FromMilliseconds(500);
                    var lastStatsUpdate = DateTimeOffset.Now;

                    await foreach (var entry in _kvStore.WatchAsync<ReadOnlySequence<byte>>(
                                       ">",
                                       opts: new NatsKVWatchOpts { MetaOnly = true },
                                       cancellationToken: ct))
                    {
                        opsBuffer.Add(entry.Operation);

                        // Update stats periodically instead of on every operation
                        if (DateTimeOffset.Now - lastStatsUpdate > statsUpdateInterval)
                        {
                            // var gets = opsBuffer.Count(op => op == NatsKVOperation.Get);
                            var puts = opsBuffer.Count(op => op == NatsKVOperation.Put);
                            var deletes = opsBuffer.Count(op => op == NatsKVOperation.Del);

                            // _stats["KeysRetrieved"] += gets;
                            _stats["KeysInserted"] += puts;
                            _stats["KeysExpired"] += deletes;

                            // Update current keys count
                            _stats["CurrentKeys"] = _stats["KeysInserted"] - _stats["KeysExpired"];

                            // Calculate operations per second
                            var elapsed = _stopwatch.Elapsed.TotalSeconds;
                            if (elapsed > 0)
                            {
                                var totalOps = _stats["KeysInserted"] + _stats["KeysRetrieved"] + _stats["KeysExpired"];
                                _stats["OperationsPerSec"] = (long)(totalOps / elapsed);
                            }

                            opsBuffer.Clear();
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
        Console.WriteLine("========== CodeCargo NatsDistributedCache Performance ==========");
        Console.WriteLine($"Keys Inserted:  {_stats["KeysInserted"],FieldWidth:N0}");
        Console.WriteLine($"Keys Retrieved: {_stats["KeysRetrieved"],FieldWidth:N0}");
        Console.WriteLine($"Keys Expired:   {_stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine($"Current Keys:   {_stats["CurrentKeys"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Ops per Second: {_stats["OperationsPerSec"],FieldWidth:N0}");
        Console.WriteLine($"Elapsed Time:   {FormatElapsedTime(_stopwatch.Elapsed),FieldWidth}");
        Console.WriteLine("==========================================================");
    }

    private void PrintFinalStats()
    {
        Console.Clear();
        Console.WriteLine("========== CodeCargo NatsDistributedCache Test Summary ==========");
        Console.WriteLine($"Test completed at: {DateTime.Now}");
        Console.WriteLine($"Total test duration: {FormatElapsedTime(_stopwatch.Elapsed)}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Total Keys Inserted:  {_stats["KeysInserted"],FieldWidth:N0}");
        Console.WriteLine($"Total Keys Retrieved: {_stats["KeysRetrieved"],FieldWidth:N0}");
        Console.WriteLine($"Total Keys Expired:   {_stats["KeysExpired"],FieldWidth:N0}");
        Console.WriteLine($"Final Current Keys:   {_stats["CurrentKeys"],FieldWidth:N0}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Average Ops per Second: {_stats["OperationsPerSec"],FieldWidth:N0}");
        Console.WriteLine("==========================================================");

        // Also log memory usage
        var meg = Math.Pow(2, 20);
        var memoryMiB = Process.GetCurrentProcess().PrivateMemorySize64 / meg;
        var allocMiB = GC.GetTotalAllocatedBytes() / meg;
        Console.WriteLine($"Memory Usage MiB: {memoryMiB,FieldWidth:N0}");
        Console.WriteLine($"Total Alloc  MiB: {allocMiB,FieldWidth:N0}");
    }
}
