-- elarion: no-transaction
CREATE INDEX CONCURRENTLY mig_events_val_idx ON mig_events (val);
