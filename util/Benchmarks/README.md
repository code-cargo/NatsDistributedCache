# Benchmarks

Micro-benchmarks comparing the cache envelope formats used by `NatsCache`:

- **JSON** — the legacy `System.Text.Json` envelope (payload base64-encoded inside a JSON object).
- **Binary** — the compact binary framing introduced in issue #37
  (`[version:1][flags:1][absExpTicks:8?][sldExpTicks:8?][payload...]`).

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

`CacheEntrySerializationBenchmarks` runs `Json_Serialize` / `Binary_Serialize` and
`Json_Deserialize` / `Binary_Deserialize` across `[Params(128, 1024, 8192, 65536, 262144)]` payload
sizes (128 B up to 256 KiB — the practical range for a NATS-backed cache, whose default `max_payload`
is 1 MB), with `Json_Serialize` as the baseline. `MemoryDiagnoser` reports `Allocated` and `Gen0`; the
`--sizes` report shows the stored byte counts and the binary/JSON ratio.

The JSON baseline is defined locally (`JsonCacheEntry`) so it stays fixed even though the library no
longer serializes with JSON.
