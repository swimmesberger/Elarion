DROP VIEW IF EXISTS mig_customer_names;
CREATE VIEW mig_customer_names AS
SELECT name, email
FROM mig_customers;
