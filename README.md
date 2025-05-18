[![NuGet Version](https://img.shields.io/nuget/v/CodeCargo.NatsDistributedCache?cacheSeconds=3600&color=516bf1)](https://www.nuget.org/packages/CodeCargo.NatsDistributedCache/)

# CodeCargo.NatsDistributedCache

## Overview

A .NET 8+ library for integrating NATS as a distributed cache in ASP.NET Core applications. Supports the new HybridCache system for fast, scalable caching.

## Requirements

- NATS 2.11 or later
- A NATS KV bucket with `LimitMarkerTTL` set for per-key TTL support. Example:
    ```csharp
    // assuming an INatsConnection natsConnection
    var kvContext = natsConnection.CreateKeyValueStoreContext();
    await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) });
    ```

## Installation

```bash
# add NATS Distributed Cache
dotnet add package CodeCargo.NatsDistributedCache

# optional - add full NATS.Net (NATS Distributed Cache uses a subset of NATS.Net dependencies)
dotnet add package NATS.Net

# optional - add HybridCache
dotnet add package Microsoft.Extensions.Caching.Hybrid
```

## Usage

See the [Full Example here](https://github.com/code-cargo/NatsDistributedCache/tree/main/util/ReadmeExample/Program.cs).
This is the portion for registering services:

```csharp
using CodeCargo.NatsDistributedCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.KeyValueStore;
using NATS.Net;

// Set the NATS URL, this normally comes from configuration
const string natsUrl = "nats://localhost:4222";

// Create a host builder for a Console application
// For a Web Application you can use WebApplication.CreateBuilder(args)
var builder = Host.CreateDefaultBuilder(args);

// Add services to the container
builder.ConfigureServices(services =>
{
    // Add NATS client
    services.AddNats(configureOpts: options => options with { Url = natsUrl });

    // Add a NATS distributed cache
    services.AddNatsDistributedCache(options =>
    {
        options.BucketName = "cache";
    });

    // (Optional) Add HybridCache
    var hybridCacheServices = services.AddHybridCache();

    // (Optional) Use NATS Serializer for HybridCache
    hybridCacheServices.AddSerializerFactory(
        NatsOpts.Default.SerializerRegistry.ToHybridCacheSerializerFactory());

    // Add other services as needed
});

// Build the host
var host = builder.Build();

// Ensure that the KV Store is created
var natsConnection = host.Services.GetRequiredService<INatsConnection>();
var kvContext = natsConnection.CreateKeyValueStoreContext();
await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache")
{
    LimitMarkerTTL = TimeSpan.FromSeconds(1)
});

// Start the host
await host.RunAsync();
```

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-9.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
