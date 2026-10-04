-- Aborts the script (CHECK violation) unless the code step between expand and contract already backfilled.
CREATE TABLE plan_check (violations INTEGER NOT NULL CHECK (violations = 0));
INSERT INTO plan_check SELECT count(*) FROM plan_items WHERE name_upper IS NULL;
