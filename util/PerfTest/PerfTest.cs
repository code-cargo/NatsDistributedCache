using System.Collections.Concurrent;
using NATS.Client.Core;

namespace CodeCargo.NatsDistributedCache.PerfTest;

public class PerfTest
{
    private readonly INatsConnection _nats;
    private readonly ConcurrentDictionary<string, long> _stats = new();

    public PerfTest(INatsConnection nats)
    {
        _nats = nats;

        // Initialize statistics counters
        _stats["KeysInserted"] = 0;
        _stats["KeysRetrieved"] = 0;
        _stats["KeysExpired"] = 0;
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Create tasks for printing stats and watching metrics
        var printTask = StartPrintTask(cts.Token);
        var watchTask = StartWatchTask(cts.Token);

        // todo: test logic
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        await cts.CancelAsync();

        // Wait for all tasks to complete or cancellation
        await Task.WhenAll(printTask, watchTask);
    }

    private Task StartPrintTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Clear
                        Console.Clear();

                        // Print current statistics
                        Console.WriteLine("======== Per Stats ========");
                        Console.WriteLine($"Keys Inserted: {_stats["KeysInserted"]}");
                        Console.WriteLine($"Keys Retrieved: {_stats["KeysRetrieved"]}");
                        Console.WriteLine($"Keys Expired: {_stats["KeysExpired"]}");
                        Console.WriteLine("===========================");

                        // Wait before printing again
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation exceptions
                }
            },
            ct);

    private static Task StartWatchTask(CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // TODO: Implement watching for cache operations and updating stats

                        // Wait before checking again
                        await Task.Delay(100, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation exceptions
                }
            },
            ct);
}
