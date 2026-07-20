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

To customize storage, replication, or size limits, use `ConfigureBucketOnCreate`. `NatsKVConfig` is an
immutable record, so return a modified copy with a `with` expression:

```csharp
using NATS.Client.KeyValueStore;

services.AddNatsDistributedCache(options =>
{
    options.BucketName = "cache";
    options.CreateBucketIfNotExists = true;
    options.ConfigureBucketOnCreate = config => config with
    {
        Storage = NatsKVStorageType.File,
        NumberOfReplicas = 3,
    };
});
```

Notes:

- Only a missing bucket is created; an existing bucket is used as-is and never modified, so
  operator-managed settings are preserved. `ConfigureBucketOnCreate` therefore only applies when the bucket is
  first created.
- Creating a bucket requires JetStream stream-management permissions.
- Overriding `History` (away from `1`) or clearing `LimitMarkerTTL` in `ConfigureBucketOnCreate` disables
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

## Cache Entry Format and Upgrades

Cache entries are stored in a compact binary envelope. When an entry cannot be deserialized — because
it was written by an incompatible release (for example a pre-binary version that used a JSON envelope)
or is otherwise corrupt — the read is treated as a **cache miss** rather than an error, and logged at
`Debug`. Because a cache's source of truth lives elsewhere, no manual migration is required:

- Entries with a TTL are reaped automatically by NATS once they expire.
- Entries without a TTL are left in place and re-populated the next time the key is written (a `Set`
  overwrites the stored bytes unconditionally), which happens naturally under cache-aside usage.

Upgrading is therefore seamless in a rolling deployment: a node never deletes an entry it cannot read,
so it cannot discard entries written by a newer node still being rolled out.

## Telemetry

Metrics and traces are emitted through `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource`.
**This package takes no dependency on OpenTelemetry.** Nothing is measured, timed, or allocated until a
listener subscribes, so registering the names below is the opt-in:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(NatsCacheTelemetryNames.MeterName))
    .WithTracing(tracing => tracing.AddSource(NatsCacheTelemetryNames.ActivitySourceName));
```

Both resolve to `CodeCargo.Nats.DistributedCache`. They are compile-time constants, so referencing them
does not initialize the meter.

### Instruments

| Name | Type | Unit | Description |
| --- | --- | --- | --- |
| `nats.cache.operation.duration` | Histogram&lt;double&gt; | `s` | Duration of cache operations |
| `nats.cache.misses` | Counter&lt;long&gt; | `{miss}` | Read misses, by reason |

There is no separate hits/operations counter: the histogram's count already gives operation rate, hit
ratio, and error rate via the `nats.cache.result` tag.

| Tag | Applies to | Values |
| --- | --- | --- |
| `nats.cache.operation` | both | `get`, `set`, `refresh`, `remove` |
| `nats.cache.bucket` | both | The configured `BucketName` |
| `nats.cache.result` | duration | `hit`, `miss`, `ok`, `error`, `cancelled` |
| `error.type` | duration | Exception type name; present only when `result=error` |
| `nats.cache.miss.reason` | misses | see below |

`nats.cache.bucket` is one value per cache instance, so it adds no meaningful cardinality while keeping
two caches in the same process distinguishable. Spans carry the same tags, plus the optional
`nats.cache.key`.

| Miss reason | Meaning |
| --- | --- |
| `not_found` | Key absent, or already reaped by the NATS TTL. The ordinary miss. |
| `expired` | Absolute expiration reached; the entry was evicted by this read. |
| `undeserializable` | Legacy or corrupt envelope (see [Cache Entry Format and Upgrades](#cache-entry-format-and-upgrades)). A sustained rate means a format migration has not drained. |
| `revision_conflict` | Lost an optimistic-concurrency race while refreshing a sliding expiration. A sustained rate means key contention. |

### Example queries

```promql
# Hit ratio. Both selectors must filter on the same operation — leaving it off the numerator would
# fold refresh hits into a denominator that counts only gets, producing a ratio above 1.
sum(rate(nats_cache_operation_duration_count{nats_cache_operation="get",nats_cache_result="hit"}[5m]))
  / sum(rate(nats_cache_operation_duration_count{nats_cache_operation="get"}[5m]))

# p99 latency by operation
histogram_quantile(0.99, sum by (le, nats_cache_operation)
  (rate(nats_cache_operation_duration_bucket[5m])))
```

### Notes

- **Cache keys are never recorded on metrics.** Set `options.Telemetry.RecordCacheKeys = true` to add the
  key to *spans* as `nats.cache.key`; it defaults to `false` because keys commonly embed user or tenant
  identifiers.
- **`TryGet` failures report `result=error`, not `result=miss`.** The read failed rather than finding
  nothing, and conflating the two would make an outage look like a cold cache. The observed miss rate a
  caller experiences is `miss + error`.
- **`TryGet` reports as `operation=get`.** The `IBufferWriter` overload is a zero-copy detail, not a
  different cache operation, so hit ratio covers all read paths.
- **Cancellation of the caller's own token is `result=cancelled`, not an error**, so shutdown-time
  cancellation does not trigger error alerts. An `OperationCanceledException` raised for any other reason —
  a NATS request timeout, for example — is reported as `result=error`, matching how the same event is
  logged, so genuine failures are never hidden behind the cancellation filter.
- An `ArgumentOutOfRangeException` from an invalid expiration passed to `Set` is not counted — that
  operation never reaches NATS.
- A miss leaves the span status `Unset`. Only genuine failures set `Error`.
- **Naming:** there is no stable OpenTelemetry semantic convention for caches, so `nats.cache.*` is a
  library-scoped prefix. `db.client.*` was rejected because NATS KV has no registered `db.system.name`
  value and cache traffic would pollute database dashboards. If OTel later stabilizes a cache convention
  it can be adopted alongside these names without breaking existing dashboards.

### Interaction with `HybridCache`

`nats.cache.*` measures **only the L2 (NATS) layer**. An L1 in-process hit produces no measurement at all,
so the hit ratio above is the hit ratio *of reads that reached NATS*, not of application cache lookups.

`Microsoft.Extensions.Caching.Hybrid` (10.7.0) reports its own combined L1+L2 telemetry through
`EventCounters` on `HybridCacheEventSource`, not through a `Meter` — so `AddMeter("Microsoft.Extensions.Caching.Hybrid")`
yields nothing, and an `EventListener` is required for the L1 view.

`NATS.Client.Core` publishes its own `ActivitySource` for messaging spans; those nest beneath the cache
spans when both sources are subscribed.

### Tuning

Omit `AddMeter`/`AddSource` to disable metrics or tracing independently. To keep hit-ratio data while
dropping the more expensive histogram:

```csharp
metrics.AddView(NatsCacheTelemetryNames.OperationDurationInstrumentName, MetricStreamConfiguration.Drop);
```

The `nats.cache.misses` counter continues to record when the histogram is dropped.

## Additional Resources

* [ASP.NET Core Hybrid Cache Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0)
* [NATS .NET Client Documentation](https://nats-io.github.io/nats.net/api/NATS.Client.Core.NatsOpts.html)
* [.NET Metrics Documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics)
* [OpenTelemetry .NET Documentation](https://opentelemetry.io/docs/languages/dotnet/)
