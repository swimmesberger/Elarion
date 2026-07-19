CREATE TABLE mig_things
(
  id    bigint PRIMARY KEY,
  label text NOT NULL
);
INSERT INTO mig_things (id, label)
VALUES (1, 'seed');
