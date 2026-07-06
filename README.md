[![CodeCargo.Nats.DistributedCache](https://img.shields.io/nuget/v/CodeCargo.Nats.DistributedCache?color=516bf1&label=CodeCargo.Nats.DistributedCache)](https://www.nuget.org/packages/CodeCargo.Nats.DistributedCache/) [![CodeCargo.Nats.HybridCacheExtensions](https://img.shields.io/nuget/v/CodeCargo.Nats.HybridCacheExtensions?color=516bf1&label=CodeCargo.Nats.HybridCacheExtensions)](https://www.nuget.org/packages/CodeCargo.Nats.HybridCacheExtensions/)

# NATS Distributed Cache

## Overview

A .NET 8+ library (tested on .NET 8 and .NET 10) for using NATS with `HybridCache` or as an `IDistributedCache` directly.

## Requirements

- NATS 2.11 or later
- A NATS KV bucket with `LimitMarkerTTL` set for per-key TTL support. Either enable
  [automatic bucket creation](#automatic-bucket-creation) (`options.CreateBucketIfNotExists = true`), or
  pre-create the bucket yourself:
    ```csharp
    using NATS.Client.KeyValueStore;
    using NATS.Net;

    // assuming an INatsConnection natsConnection
    var kvContext = natsConnection.CreateKeyValueStoreContext();
    await kvContext.CreateOrUpdateStoreAsync(
        new NatsKVConfig("cache") { LimitMarkerTTL = TimeSpan.FromSeconds(1), History = 1 });
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
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Set the NATS URL, this normally comes from configuration
const string natsUrl = "nats://localhost:4222";

// Create a host builder for a Console application
// For a Web Application you can use WebApplication.CreateBuilder(args)
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNatsClient(natsBuilder =>
        natsBuilder.ConfigureOptions(optsBuilder => optsBuilder.Configure(opts =>
            opts.Opts = opts.Opts with { Url = natsUrl })));
    services.AddNatsHybridCache(options =>
    {
        options.BucketName = "cache";

        // Create the KV bucket on first use if it doesn't already exist.
        // Omit this if you pre-create the bucket yourself (see Requirements).
        options.CreateBucketIfNotExists = true;
    });
});

var host = builder.Build();

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
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

// Set the NATS URL, this normally comes from configuration
const string natsUrl = "nats://localhost:4222";

// Create a host builder for a Console application
// For a Web Application you can use WebApplication.CreateBuilder(args)
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices(services =>
{
    services.AddNatsClient(natsBuilder =>
        natsBuilder.ConfigureOptions(optsBuilder => optsBuilder.Configure(opts =>
            opts.Opts = opts.Opts with { Url = natsUrl })));
    services.AddNatsDistributedCache(options =>
    {
        options.BucketName = "cache";

        // Create the KV bucket on first use if it doesn't already exist.
        // Omit this if you pre-create the bucket yourself (see Requirements).
        options.CreateBucketIfNotExists = true;
    });
});

var host = builder.Build();

// Start the host
await host.RunAsync();
```

## Automatic bucket creation

By default the KV bucket must already exist. Set `CreateBucketIfNotExists = true` to have the cache
create it on first use if it is missing, with the settings per-key TTL requires —
`History = 1` and a non-zero `LimitMarkerTTL`:

```csharp
services.AddNatsDistributedCache(options =>
{
    options.BucketName = "cache";
    options.CreateBucketIfNotExists = true;
});
```

To customize storage, replication, or size limits, use `ConfigureBucket`. `NatsKVConfig` is an
immutable record, so return a modified copy with a `with` expression:

```csharp
using NATS.Client.KeyValueStore;

services.AddNatsDistributedCache(options =>
{
    options.BucketName = "cache";
    options.CreateBucketIfNotExists = true;
    options.ConfigureBucket = config => config with
    {
        Storage = NatsKVStorageType.File,
        NumberOfReplicas = 3,
    };
});
```

Notes:

- Only a missing bucket is created; an existing bucket is used as-is and never modified, so
  operator-managed settings are preserved. `ConfigureBucket` therefore only applies when the bucket is
  first created.
- Creating a bucket requires JetStream stream-management permissions.
- Overriding `History` (away from `1`) or clearing `LimitMarkerTTL` in `ConfigureBucket` disables
  reliable per-key TTL.

## Controlling Expiration Timing

Expiration is computed from a [`TimeProvider`](https://learn.microsoft.com/dotnet/api/system.timeprovider),
defaulting to `TimeProvider.System`. Register a `TimeProvider` in DI to override the clock the cache uses —
for example, to drive expiration deterministically in tests with
[`FakeTimeProvider`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.time.testing.faketimeprovider):

```csharp
services.AddSingleton<TimeProvider>(new FakeTimeProvider());
services.AddNatsDistributedCache(options => options.BucketName = "cache");
```

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
