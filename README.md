[![CodeCargo.NatsDistributedCache](https://img.shields.io/nuget/v/CodeCargo.NatsDistributedCache?color=516bf1&label=CodeCargo.NatsDistributedCache)](https://www.nuget.org/packages/CodeCargo.NatsDistributedCache/) [![CodeCargo.NatsHybridCache](https://img.shields.io/nuget/v/CodeCargo.NatsHybridCache?color=516bf1&label=CodeCargo.NatsHybridCache)](https://www.nuget.org/packages/CodeCargo.NatsHybridCache/)

# CodeCargo.NatsDistributedCache

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

## Installation

```bash
# add NATS Distributed Cache
dotnet add package CodeCargo.NatsDistributedCache
dotnet add package CodeCargo.NatsHybridCache

# optional - add full NATS.Net (NATS Distributed Cache uses a subset of NATS.Net dependencies)
dotnet add package NATS.Net

# optional - add HybridCache
dotnet add package Microsoft.Extensions.Caching.Hybrid
```


## Use with `HybridCache`

The `CodeCargo.NatsHybridCache` package integrates HybridCache with NATS. It registers the distributed cache and configures HybridCache to use the NATS serializer registry.

### Install

```bash
dotnet add package CodeCargo.NatsDistributedCache
dotnet add package CodeCargo.NatsHybridCache
dotnet add package NATS.Net
```

### Example

```csharp
using CodeCargo.NatsHybridCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.KeyValueStore;
using NATS.Net;

const string natsUrl = "nats://localhost:4222";
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNats(configureOpts: options => options with { Url = natsUrl });

    services.AddNatsHybridCache(options =>
    {
        options.BucketName = "cache";
    });
});

var host = builder.Build();
var natsConnection = host.Services.GetRequiredService<INatsConnection>();
var kvContext = natsConnection.CreateKeyValueStoreContext();
await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache")
{
    LimitMarkerTTL = TimeSpan.FromSeconds(1)
});

await host.RunAsync();
```

## Use `IDistributedCache` Directly

### Install

```bash
dotnet add package CodeCargo.NatsDistributedCache
dotnet add package NATS.Net
```

### Example

```csharp
using CodeCargo.NatsDistributedCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.Hosting;
using NATS.Client.KeyValueStore;
using NATS.Net;

const string natsUrl = "nats://localhost:4222";
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNats(configureOpts: options => options with { Url = natsUrl });

    services.AddNatsDistributedCache(options =>
    {
        options.BucketName = "cache";
    });
});

var host = builder.Build();
var natsConnection = host.Services.GetRequiredService<INatsConnection>();
var kvContext = natsConnection.CreateKeyValueStoreContext();
await kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig("cache")
{
    LimitMarkerTTL = TimeSpan.FromSeconds(1)
});

await host.RunAsync();
```

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-9.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
