-- elarion: no-transaction
INSERT OR IGNORE INTO mig_points (id) VALUES (1);
ALTER TABLE mig_points ADD COLUMN extra TEXT;
