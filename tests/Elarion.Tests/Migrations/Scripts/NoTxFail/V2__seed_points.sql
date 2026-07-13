-- elarion: no-transaction
INSERT INTO mig_points (id, val) VALUES (1, 10);
ALTER TABLE mig_points_missing ADD COLUMN broken int;
