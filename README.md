[![CodeCargo.Nats.DistributedCache](https://img.shields.io/nuget/v/CodeCargo.Nats.DistributedCache?color=516bf1&label=CodeCargo.Nats.DistributedCache)](https://www.nuget.org/packages/CodeCargo.Nats.DistributedCache/) [![CodeCargo.Nats.HybridCacheExtensions](https://img.shields.io/nuget/v/CodeCargo.Nats.HybridCacheExtensions?color=516bf1&label=CodeCargo.Nats.HybridCacheExtensions)](https://www.nuget.org/packages/CodeCargo.Nats.HybridCacheExtensions/)

# NATS Distributed Cache

## Overview

A .NET 8+ library for using NATS with `HybridCache` or as an `IDistributedCache` directly.

## Requirements

- NATS 2.11 or later
- A NATS KV bucket with `LimitMarkerTTL` set for per-key TTL support. Example:
    ```csharp
    // assuming an INatsConnection natsConnection
    var kvContext = natsConnection.CreateKeyValueStoreContext();
    await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) });
    ```

## Use with `HybridCache`

The `CodeCargo.Nats.HybridCacheExtensions` package provides an extension method that:

1. Adds the NATS `IDistributedCache`
2. Adds `HybridCache`
3. Configures `HybridCache` to use the NATS Connection's serializer registry

### Install

```bash
dotnet add package CodeCargo.Nats.HybridCacheExtensions
dotnet add package NATS.Extensions.Microsoft.DependencyInjection
```

### Example

```csharp
using CodeCargo.Nats.HybridCacheExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Set the NATS URL, this normally comes from configuration
const string natsUrl = "nats://localhost:4222";

// Create a host builder for a Console application
// For a Web Application you can use WebApplication.CreateBuilder(args)
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNatsClient(natsBuilder => natsBuilder.ConfigureOptions(opts => opts with { Url = natsUrl }));
    services.AddNatsHybridCache(options =>
    {
        options.BucketName = "cache";
    });
});

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

## Use `IDistributedCache` Directly

### Install

```bash
dotnet add package CodeCargo.Nats.DistributedCache
dotnet add package NATS.Extensions.Microsoft.DependencyInjection
```

### Example

```csharp
using CodeCargo.Nats.DistributedCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Set the NATS URL, this normally comes from configuration
const string natsUrl = "nats://localhost:4222";

// Create a host builder for a Console application
// For a Web Application you can use WebApplication.CreateBuilder(args)
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNatsClient(natsBuilder => natsBuilder.ConfigureOptions(opts => opts with { Url = natsUrl }));
    services.AddNatsDistributedCache(options =>
    {
        options.BucketName = "cache";
    });
});

var host = builder.Build();

// Ensure that the KV Store is created
var natsConnection = host.Services.GetRequiredService<INatsConnection>();
var kvContext = natsConnection.CreateKeyValueStoreContext();
await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1) });

// Start the host
await host.RunAsync();
```

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-9.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
