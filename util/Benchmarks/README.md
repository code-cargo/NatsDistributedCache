# Benchmarks

Micro-benchmarks for the `NatsCache` value envelope — the framing that wraps every cached payload
together with its expiration metadata before it is stored in NATS KV.

Today this measures the **JSON** envelope (payload base64-encoded inside a JSON object), establishing a
baseline. Issue #37 replaces it with a compact binary framing and extends these benchmarks with a
`Binary` variant for a side-by-side comparison.

These are in-memory serialize/deserialize benchmarks — **no NATS server or Docker required**.

## Run

Serialized-size comparison (fast, deterministic):

```bash
dotnet run -c Release --project util/Benchmarks -f net10.0 -- --sizes
```

Timing + allocation benchmarks (BenchmarkDotNet, `MemoryDiagnoser`):

```bash
# net10.0
dotnet run -c Release --project util/Benchmarks -f net10.0 -- --filter '*'

# net8.0
dotnet run -c Release --project util/Benchmarks -f net8.0 -- --filter '*'
```

Filter to a subset, e.g. only serialize benchmarks:

```bash
dotnet run -c Release --project util/Benchmarks -f net10.0 -- --filter '*_Serialize'
```

## What is measured

`CacheEntrySerializationBenchmarks` runs `Json_Serialize` / `Json_Deserialize` across
`[Params(128, 1024, 8192, 65536, 262144)]` payload sizes (128 B up to 256 KiB — the practical range
for a NATS-backed cache, whose default `max_payload` is 1 MB). `MemoryDiagnoser` reports `Allocated`
and `Gen0`; the `--sizes` report shows the stored byte counts.

The JSON baseline is defined locally (`JsonCacheEntry`) so it stays fixed as the library evolves.
