var builder = DistributedApplication.CreateBuilder(args);

// Redis container configuration
// Let Aspire dynamically assign port to avoid conflicts
var redis = builder.AddRedis("Redis")
    .WithImage("redis")
    .WithImageTag("8.0.1");

builder.Build().Run();
