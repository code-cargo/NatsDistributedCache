using CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

BaseTestProvider provider;

// Check if Redis tests should be run based on command line args or environment variable
var useRedis = args.Contains("--redis") ||
               (Environment.GetEnvironmentVariable("TEST_REDIS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ??
                false);

if (useRedis)
{
    Console.WriteLine("Running Redis tests...");
    provider = new RedisTestProvider();
}
else
{
    Console.WriteLine("Running NATS tests...");
    provider = new NatsTestProvider();
}

await provider.RunAsync(args);
