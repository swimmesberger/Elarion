# EdgeTelemetry — the Elarion EF-free NativeAOT tier

A telemetry ingest node published as a **native binary**: devices POST readings, operators query
latest values, recent history, and hourly aggregates. The use case is **cold-start speed and
footprint** — edge boxes, scale-to-zero deployments, sidecars, CLI-style lifetimes — so the sample is
built on the tier Elarion ships for exactly that:

```
Elarion.Migrations.PostgreSql  (ADR-0057)   the schema half: embedded V__ scripts, applied
        │                                   before the host reports ready, fail-closed
        ▼
PostgreSQL (the one you already run)
        ▲
Elarion.Sql                    (ADR-0058)   the access half: [SqlRecord] → generated
                                            ISqlRowMapper<T> + injection-safe interpolation
```

No EF, no reflection, no runtime code generation. Every row mapper, JSON contract, and route handler
is compiled ahead of time; `JsonSerializerIsReflectionEnabledByDefault=false` holds for the whole
process. If a row type doesn't map, the **build** fails (`ELSQL001`–`ELSQL007`) — there is no
reflection fallback to limp along on, which is the property that makes NativeAOT safe here at all.

## Measured (Apple M4 Pro, .NET 10, PostgreSQL 17 in a local container, 2026-07)

| | |
| --- | --- |
| Publish output | **17 MB** self-contained native executable, **zero** trim/AOT warnings |
| Cold start, fresh database | **≈113 ms** to the first `200 /healthz` — including opening the pool, taking the advisory lock, and applying the migration |
| Warm restarts (schema current) | ≈115–160 ms to first response |

## Run it

```bash
# a PostgreSQL to talk to (or point ConnectionStrings__Telemetry at your own)
podman run -d --name edge-pg -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:17-alpine
podman exec edge-pg psql -U postgres -c "CREATE DATABASE edge_telemetry"

# JIT for the inner loop…
dotnet run --project samples/EdgeTelemetry/EdgeTelemetry.Api

# …NativeAOT for the real thing
dotnet publish samples/EdgeTelemetry/EdgeTelemetry.Api -c Release -o out
./out/EdgeTelemetry.Api
```

```bash
curl -X POST localhost:5217/readings -H 'Content-Type: application/json' -d '[
  {"deviceId":"edge-1","metric":"temperature","value":21.5},
  {"deviceId":"edge-1","metric":"temperature","value":23.0,
   "meta":{"unit":"°C","source":"sensor-a","quality":3}}]'

curl "localhost:5217/devices/edge-1/latest?metric=temperature"
curl "localhost:5217/devices/edge-1/history?metric=temperature&limit=50"
curl "localhost:5217/devices/edge-1/stats?metric=temperature&hours=24"
```

## What to look at

- **`Telemetry.cs`** — the whole data contract. `ReadingRow` is a `[SqlRecord]`: the generated
  `ReadingRowSqlMapper` carries ordinal-cached typed reads, typed `BindParameters`, and the
  `TableName`/`Columns` constants the endpoints compose SQL from. `MetricBucket` shows that a
  `[SqlRecord]` needs no physical table — it binds the hourly-aggregate projection. The `[SqlJson]`
  meta column rides the same source-generated `JsonSerializerContext` as the HTTP contracts, through
  Elarion's canonical JSON accessor (ADR-0023).
- **`Program.cs`** — the slim builder plus exactly two Elarion registrations:
  `ConfigureElarionJson` + `AddElarionSqlMappers()` (generated for this assembly), and
  `AddElarionPostgreSqlMigrations(dataSource, …)` for schema-before-traffic. The stats endpoint is
  the "full power of SQL" leg: `date_trunc` + aggregates stay SQL; interpolated holes (`{deviceId}`,
  `{since}`) bind as parameters; `{…:raw}` splices only the generated constants.
- **`Migrations/V20260714090000__create_readings.sql`** — the schema is plain SQL, embedded in the
  binary, checksummed, and applied under a session-level advisory lock before the first request.
- **`EdgeTelemetry.Tests`** — the whole slice end-to-end against real PostgreSQL (Testcontainers,
  Docker-gated skip): startup migrates, ingest binds through the generated mapper, queries read back
  through interpolated SQL — jsonb metadata included.

## Why this shape

The other samples show Elarion's default tier: [Billing](../Billing) is the layered EF host,
[LiveQuotes](../LiveQuotes) the realtime in-memory middle ground. EdgeTelemetry is the third
position: when the deployment target punishes cold start and footprint, drop EF — not correctness.
The [SQL mapping](https://elarion.wimmesberger.dev/docs/capabilities/sql-mapping) and
[SQL migrations](https://elarion.wimmesberger.dev/docs/capabilities/sql-migrations) docs carry the
full tier decision table; the short version is the first row of each: **if you can use EF Core, use
EF Core.** This tier exists for the hosts that can't.

The mapper itself is benchmarked at **hand-written ADO.NET parity in time and allocations**
(`tests/Elarion.Benchmarks`, `--filter "*SqlMapping*"`), so choosing this tier costs no runtime
performance over the code you would have written by hand — it just stops you writing it.
