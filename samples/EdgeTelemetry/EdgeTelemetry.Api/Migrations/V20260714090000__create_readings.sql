CREATE TABLE readings (
    id uuid PRIMARY KEY,
    device_id text NOT NULL,
    metric text NOT NULL,
    recorded_at timestamptz NOT NULL,
    value double precision NOT NULL,
    meta jsonb NULL
);

-- The one query shape this node serves: per device + metric, newest first.
CREATE INDEX readings_device_metric_time ON readings (device_id, metric, recorded_at DESC);
