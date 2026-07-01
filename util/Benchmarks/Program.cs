using BenchmarkDotNet.Running;
using CodeCargo.Nats.DistributedCache.Benchmarks;

// `--sizes` prints the serialized-size comparison table and exits; anything else is handed to
// BenchmarkDotNet (e.g. `--filter *Serialize*`).
if (args.Contains("--sizes"))
{
    SizeReport.Print();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(CacheEntrySerializationBenchmarks).Assembly).Run(args);
