-- The ADR-0056 posture: extensions are composition, not scale-out — TimescaleDB ships in the server
-- image (timescale/timescaledb:*-pg17), and enabling it is an ordinary migration in the same history
-- as everything else. The whole block is transactional: extension + table + hypertable + policy
-- commit together with this script's history row, or not at all.
CREATE
EXTENSION IF NOT EXISTS timescaledb;

-- TimescaleDB's one rule: every unique constraint — the primary key included — must contain the
-- partition column. The composite natural key satisfies it AND makes ingest idempotent by
-- constraint: a retransmitted batch hits the key and inserts nothing (ON CONFLICT DO NOTHING).
CREATE TABLE readings
(
  device_id   text             NOT NULL,
  metric      text             NOT NULL,
  recorded_at timestamptz      NOT NULL,
  value       double precision NOT NULL,
  meta        jsonb NULL,
  PRIMARY KEY (device_id, metric, recorded_at)
);

SELECT create_hypertable('readings', by_range('recorded_at'));

-- Retention runs in-database with zero application code (the alternative is an Elarion scheduled
-- job when the window must live in app configuration — see the time-series recipe).
SELECT add_retention_policy('readings', INTERVAL '90 days');
