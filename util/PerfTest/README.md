# NATS/Redis Distributed Cache Performance Test

This utility performs performance testing of distributed cache implementations, comparing NATS and Redis backends for the `IDistributedCache` interface in .NET.

## Overview

The performance test runs a series of operations against the distributed cache implementation:

1. **Insert** - Adds items with absolute expiration
2. **Get** - Retrieves previously added items
3. **Update** - Updates items with sliding expiration
4. **Get (refresh)** - Retrieves item, extending sliding expiration
5. **Delete** - Removes items

Each operation is timed and metrics are collected, including:
- Total operations completed
- Operations per second
- Data throughput
- P50/P95/P99 latency percentiles

## Running Tests

By default, the performance test runs against the NATS implementation:

```bash
dotnet run -c Release
```

To run Redis tests instead, you can:

```bash
# flag
dotnet run -c Release -- --redis

# or env var
TEST_REDIS=true dotnet run -c Release
```

## Test Configuration

The test performs operations on:
- 20,000 unique keys
- 128-byte value payload per key
- Parallelism based on the available processor count

## Implementation Details

The test uses Aspire's distributed application model to:
- Create and manage required infrastructure (NATS server or Redis instance)
- Configure the appropriate `IDistributedCache` implementation
- Run the performance tests in parallel
- Collect and display metrics

The test providers are implemented as:
- `NatsTestProvider` - For NATS-backed distributed cache
- `RedisTestProvider` - For Redis-backed distributed cache
