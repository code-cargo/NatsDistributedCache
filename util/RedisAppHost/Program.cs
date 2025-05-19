var builder = DistributedApplication.CreateBuilder(args);

// Redis container configuration
builder.AddRedis("Redis")
    .WithImage("redis")
    .WithImageTag("8.0.1")
    .WithEndpoint(port: 16379, targetPort: 6379, name: "redis", scheme: "tcp");

builder.Build().Run();
