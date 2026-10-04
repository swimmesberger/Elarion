-- Fails (NULLs remain) unless the code step between expand and contract already backfilled the column.
ALTER TABLE plan_items ALTER COLUMN name_upper SET NOT NULL;
