![NuGet Version](https://img.shields.io/nuget/v/CodeCargo.Nats.DistributedCache?cacheSeconds=3600&color=516bf1)

# CodeCargo.Nats.DistributedCache

## Overview

A .NET 8+ library for integrating NATS as a distributed cache in ASP.NET Core applications. Supports the new HybridCache system for fast, scalable caching.

## Requirements

- NATS 2.11 or later
- A NATS KV bucket with `LimitMarkerTTL` set for per-key TTL support. Example:
    ```csharp
    // assuming an INatsConnection natsConnection
    var kvContext = natsConnection.CreateKeyValueStoreContext();
    await kvContext.CreateOrUpdateStoreAsync(
        new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1), });
    ```

## Installation

```bash
dotnet add package CodeCargo.Nats.DistributedCache
```

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using CodeCargo.NatsDistributedCache;

var builder = WebApplication.CreateBuilder(args);

// Add a NATS connection
var natsOpts = NatsOpts.Default with { Url = "nats://localhost:4222" }
builder.Services.AddNats(opts => natsOpts);

// Add a NATS distributed cache
builder.Services.AddNatsDistributedCache(options =>
{
    options.BucketName = "cache";
});

// (Optional) Add HybridCache
var hybridCacheServices = builder.Services.AddHybridCache();

// (Optional) Use NATS Serializer for HybridCache
hybridCacheServices.AddSerializerFactory(
  natsOpts.SerializerRegistry.ToHybridCacheSerializerFactory());

var app = builder.Build();
app.Run();
```

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-9.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
