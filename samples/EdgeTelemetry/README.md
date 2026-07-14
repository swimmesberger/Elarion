# EdgeTelemetry — the Elarion EF-free NativeAOT tier, on TimescaleDB

A telemetry ingest node published as a **native binary**: devices POST readings, operators query
latest values, recent history, and hourly aggregates from a **TimescaleDB hypertable**. The use case
is **cold-start speed and footprint** — edge boxes, scale-to-zero deployments, sidecars, CLI-style
lifetimes — so the sample is built on the tier Elarion ships for exactly that, composed with the
extension posture from ADR-0056:

```
Elarion.Migrations.PostgreSql  (ADR-0057)   the schema half: embedded V__ scripts, applied
        │                                   before the host reports ready, fail-closed
        ▼
TimescaleDB (ADR-0056: the extension rides the server image;
             enabling it is an ordinary migration — CREATE EXTENSION,
             create_hypertable, add_retention_policy, one transaction)
        ▲
Elarion.Sql                    (ADR-0058)   the access half: [SqlRecord] → generated
                                            ISqlRowMapper<T> + injection-safe interpolation
```

No EF, no reflection, no runtime code generation. Every row mapper, JSON contract, and route handler
is compiled ahead of time; `JsonSerializerIsReflectionEnabledByDefault=false` holds for the whole
process. If a row type doesn't map, the **build** fails (`ELSQL001`–`ELSQL007`) — there is no
reflection fallback to limp along on, which is the property that makes NativeAOT safe here at all.

And because `Elarion.Sql` is hand-written SQL, the extension needs **zero framework support**:
`time_bucket`, `create_hypertable`, and the retention policy are just SQL — exactly the ADR-0056
claim that a specialized workload is a *recipe over the one PostgreSQL*, never a subsystem.

## Measured (Apple M4 Pro, .NET 10, TimescaleDB pg17 in a local container, 2026-07)

| | |
| --- | --- |
| Publish output | **17 MB** self-contained native executable, **zero** trim/AOT warnings |
| Cold start, fresh database | **≈650 ms** to the first `200 /healthz` — dominated by the one-time `CREATE EXTENSION timescaledb` + `create_hypertable`; the runner, lock, and history bookkeeping are noise |
| Warm restarts (schema current) | **≈120–160 ms** to first response, migration validation included |

## Run it

```bash
# the recipe image (ADR-0056): TimescaleDB ships in the server image everywhere
podman run -d --name edge-ts -p 5432:5432 -e POSTGRES_PASSWORD=postgres timescale/timescaledb:latest-pg17
podman exec edge-ts psql -U postgres -c "CREATE DATABASE edge_telemetry"

# JIT for the inner loop…
dotnet run --project samples/EdgeTelemetry/EdgeTelemetry.Api

# …NativeAOT for the real thing
dotnet publish samples/EdgeTelemetry/EdgeTelemetry.Api -c Release -o out
./out/EdgeTelemetry.Api
```

```bash
curl -X POST localhost:5217/readings -H 'Content-Type: application/json' -d '[
  {"deviceId":"edge-1","metric":"temperature","value":21.5,"recordedAt":"2026-07-14T10:00:00Z"},
  {"deviceId":"edge-1","metric":"temperature","value":23.0,"recordedAt":"2026-07-14T10:30:00Z",
   "meta":{"unit":"°C","source":"sensor-a","quality":3}}]'
# → {"written":2}   — POST the same batch again: {"written":0}, idempotent by constraint

curl "localhost:5217/devices/edge-1/latest?metric=temperature"
curl "localhost:5217/devices/edge-1/history?metric=temperature&limit=50"
curl "localhost:5217/devices/edge-1/stats?metric=temperature&hours=24"    # time_bucket rollup
```

## What to look at

- **`Migrations/V20260714090000__create_readings.sql`** — the whole TimescaleDB story is one
  transactional migration in the same history as everything else: `CREATE EXTENSION`, the table with
  its **composite natural key** (TimescaleDB's rule: every unique constraint must contain the
  partition column — and the same key makes ingest idempotent), `create_hypertable`, and an
  in-database `add_retention_policy` (zero application code; use an Elarion scheduled job instead
  when the window must live in app configuration).
- **`Telemetry.cs`** — the whole data contract. `ReadingRow` is a `[SqlRecord]`: the generated
  `ReadingRowSqlMapper` carries ordinal-cached typed reads, typed `BindParameters`, and the
  `TableName`/`Columns` constants the endpoints compose SQL from. `MetricBucket` shows that a
  `[SqlRecord]` needs no physical table — it binds the `time_bucket` projection. The `[SqlJson]`
  meta column rides the same source-generated `JsonSerializerContext` as the HTTP contracts, through
  Elarion's canonical JSON accessor (ADR-0023).
- **`Program.cs`** — the slim builder plus exactly two Elarion registrations:
  `ConfigureElarionJson` + `AddElarionSqlMappers()` (generated for this assembly), and
  `AddElarionPostgreSqlMigrations(dataSource, …)` for schema-before-traffic. Ingest is one
  transaction per batch with `ON CONFLICT DO NOTHING` — retransmitted device batches insert nothing.
  The stats endpoint is the "full power of SQL" leg: `time_bucket` + aggregates stay SQL;
  interpolated holes (`{deviceId}`, `{since}`) bind as parameters; `{…:raw}` splices only the
  generated constants.
- **`EdgeTelemetry.Tests`** — the whole slice end-to-end against real TimescaleDB (Testcontainers,
  Docker-gated skip): startup migrates (extension + hypertable included), ingest binds through the
  generated mapper, a retransmit writes zero rows, and the rollup reads back through the projection
  mapper.

## Why this shape

The other samples show Elarion's default tier: [Billing](../Billing) is the layered EF host,
[LiveQuotes](../LiveQuotes) the realtime in-memory middle ground. EdgeTelemetry is the third
position: when the deployment target punishes cold start and footprint, drop EF — not correctness.
The [SQL mapping](https://elarion.wimmesberger.dev/docs/capabilities/sql-mapping) and
[SQL migrations](https://elarion.wimmesberger.dev/docs/capabilities/sql-migrations) docs carry the
full tier decision table; the short version is the first row of each: **if you can use EF Core, use
EF Core.** This tier exists for the hosts that can't. The
[time series](https://elarion.wimmesberger.dev/docs/capabilities/time-series) recipe shows the same
TimescaleDB composition on the EF tier;
[PostgreSQL extensions](https://elarion.wimmesberger.dev/docs/capabilities/postgres-extensions)
carries the image guidance.

The mapper itself is benchmarked at **hand-written ADO.NET parity in time and allocations**
(`tests/Elarion.Benchmarks`, `--filter "*SqlMapping*"`), so choosing this tier costs no runtime
performance over the code you would have written by hand — it just stops you writing it.
