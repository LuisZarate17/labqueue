-- labqueue seed verification. Every row of output must read PASS.
--
--   docker compose exec -T db psql -U labqueue -d labqueue -f /seed/03_verify.sql

\set ON_ERROR_STOP on
\timing off
\pset footer off

\echo ''
\echo '=== 1. no two confirmed reservations overlap on the same resource ==='
-- Window function rather than a self-join: there is no index on this table and a
-- self-join over half a million rows does not come back.
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL (' || count(*) || ' overlapping pairs)' END AS result
FROM (
  SELECT resource_id, id, during,
         lag(upper(during)) OVER (PARTITION BY resource_id ORDER BY lower(during)) AS prev_upper
  FROM reservations WHERE status = 'confirmed'
) t
WHERE prev_upper > lower(during);

\echo ''
\echo '=== 2. every resource carries enough confirmed rows to be worth indexing ==='
SELECT CASE WHEN min(n) >= 200 THEN 'PASS' ELSE 'FAIL' END AS result,
       min(n) AS min_per_resource, round(avg(n)) AS avg_per_resource,
       max(n) AS max_per_resource, count(*) AS resources
FROM (SELECT resource_id, count(*) AS n FROM reservations WHERE status = 'confirmed' GROUP BY resource_id) t;

\echo ''
\echo '=== 3. cancelled rows really do overlap confirmed ones ==='
-- If they did not, the partial predicate on the eventual constraint would be
-- untested by this seed.
SELECT CASE WHEN count(*) > 0 THEN 'PASS' ELSE 'FAIL (no overlapping cancelled rows)' END AS result,
       count(*) AS overlapping_cancelled
FROM reservations c
WHERE c.status = 'cancelled'
  AND EXISTS (SELECT 1 FROM reservations f
              WHERE f.status = 'confirmed' AND f.resource_id = c.resource_id AND f.during && c.during);

\echo ''
\echo '=== 4. maintenance windows never collide with confirmed reservations ==='
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL (' || count(*) || ' collisions)' END AS result
FROM maintenance_windows m
WHERE EXISTS (SELECT 1 FROM reservations r
              WHERE r.status = 'confirmed' AND r.resource_id = m.resource_id AND r.during && m.during);

\echo ''
\echo '=== 5. bound constraints exist on both range tables ==='
SELECT CASE WHEN count(*) = 2 THEN 'PASS' ELSE 'FAIL' END AS result,
       string_agg(conname, ', ' ORDER BY conname) AS constraints
FROM pg_constraint
WHERE contype = 'c' AND conname IN ('reservations_during_bounds', 'maintenance_windows_during_bounds');

\echo ''
\echo '=== 6. reservations carries no index other than its primary key ==='
SELECT CASE WHEN count(*) = 0 THEN 'PASS' ELSE 'FAIL' END AS result,
       coalesce(string_agg(indexname, ', '), '(none)') AS unexpected_indexes
FROM pg_indexes WHERE tablename = 'reservations' AND indexname <> 'PK_reservations';

\echo ''
\echo '=== 7. the database rejects a []-bounded range ==='
BEGIN;
DO $$
DECLARE
  probe_resource uuid := (SELECT id FROM resources LIMIT 1);
  probe_user     uuid := (SELECT id FROM users LIMIT 1);
BEGIN
  INSERT INTO reservations (id, resource_id, user_id, during, status)
  VALUES (gen_random_uuid(), probe_resource, probe_user,
          tstzrange(TIMESTAMPTZ '2030-01-01 09:00+00', TIMESTAMPTZ '2030-01-01 11:00+00', '[]'),
          'confirmed');
  RAISE EXCEPTION 'FAIL: a []-bounded range was accepted';
EXCEPTION
  WHEN check_violation THEN RAISE NOTICE 'PASS: []-bounded range rejected by the database';
END $$;
ROLLBACK;

\echo ''
\echo '=== 8. the eventual overlap constraint can still be created ==='
-- Added and rolled back. ADD CONSTRAINT ... EXCLUDE validates every existing row,
-- so proving it here is what stops that failing later against 500k rows.
BEGIN;
ALTER TABLE reservations
  ADD CONSTRAINT reservations_no_overlap_probe
  EXCLUDE USING gist (resource_id WITH =, during WITH &&)
  WHERE (status = 'confirmed');
SELECT 'PASS: exclusion constraint validated against every existing row' AS result;
ROLLBACK;

\echo ''
\echo '=== totals ==='
SELECT
  (SELECT count(*) FROM reservations WHERE status = 'confirmed') AS confirmed,
  (SELECT count(*) FROM reservations WHERE status = 'cancelled') AS cancelled,
  (SELECT count(*) FROM maintenance_windows)                     AS maintenance_windows,
  (SELECT count(*) FROM resources)                               AS resources,
  (SELECT count(*) FROM users)                                   AS users;
