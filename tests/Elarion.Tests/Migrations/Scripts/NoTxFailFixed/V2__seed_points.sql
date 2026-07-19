-- elarion: no-transaction
INSERT INTO mig_points (id, val)
VALUES (1, 10) ON CONFLICT (id) DO NOTHING;
ALTER TABLE mig_points
  ADD COLUMN IF NOT EXISTS extra int;
