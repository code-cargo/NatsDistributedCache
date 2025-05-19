using CodeCargo.Nats.DistributedCache.PerfTest.TestProvider;

BaseTestProvider provider;
Console.WriteLine("Select test provider:");
Console.WriteLine("1) NATS (default)");
Console.WriteLine("2) Redis");
var choice = Console.ReadLine();
provider = choice?.Trim() == "2" ? new RedisTestProvider() : new NatsTestProvider();

await provider.RunAsync(args);
