-- The availability overlap query, captured for the record.
--
--   docker compose exec -T db psql -U labqueue -d labqueue -f /db/explain/availability.sql
--
-- Phase 06 re-runs this file unchanged against the same seed, so any difference
-- between runs is the index under test and nothing else. The resource id is
-- derived the same way the seed derives it, so it survives a reload.

\set ON_ERROR_STOP on
\pset footer off

\echo '=== environment ==='
SELECT
  current_setting('server_version')   AS server_version,
  current_setting('shared_buffers')   AS shared_buffers,
  current_setting('work_mem')         AS work_mem,
  current_setting('effective_cache_size') AS effective_cache_size,
  (SELECT count(*) FROM reservations WHERE status = 'confirmed') AS confirmed_rows,
  pg_size_pretty(pg_total_relation_size('reservations'))         AS reservations_size;

\echo ''
\echo '=== indexes present on reservations ==='
SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'reservations' ORDER BY indexname;

\echo ''
\echo '=== query ==='
\echo 'SELECT * FROM reservations'
\echo 'WHERE resource_id = <RES-100> AND status = ''confirmed'''
\echo '  AND during && tstzrange(''2026-06-01+00'', ''2026-06-08+00'', ''[)'');'

-- Warm-up run, discarded. The measured run should not be the one that faults the
-- table into shared buffers.
\o /dev/null
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM reservations
WHERE resource_id = md5('labqueue:resource:100')::uuid
  AND status = 'confirmed'
  AND during && tstzrange(TIMESTAMPTZ '2026-06-01 00:00:00+00', TIMESTAMPTZ '2026-06-08 00:00:00+00', '[)');
\o

\echo ''
\echo '=== EXPLAIN (ANALYZE, BUFFERS) — measured run ==='
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM reservations
WHERE resource_id = md5('labqueue:resource:100')::uuid
  AND status = 'confirmed'
  AND during && tstzrange(TIMESTAMPTZ '2026-06-01 00:00:00+00', TIMESTAMPTZ '2026-06-08 00:00:00+00', '[)');

-- ---------------------------------------------------------------------------
-- The same query in the form the application actually sends it.
--
-- EF Core emits the statement below with bind parameters, not literals. The
-- column list is every column on the table, so it is "SELECT *" in all but
-- spelling. Explaining only the literal form above would describe a query the
-- application never runs, and would miss any custom-vs-generic plan difference.
-- ---------------------------------------------------------------------------

PREPARE availability (uuid, tstzrange) AS
SELECT r.id, r.cancelled_at, r.created_at, r.during, r.resource_id, r.status, r.user_id
FROM reservations AS r
WHERE r.resource_id = $1 AND r.status = 'confirmed' AND r.during && $2;

\echo ''
\echo '=== EXPLAIN — EF Core statement, custom plan ==='
SET plan_cache_mode = force_custom_plan;
EXPLAIN (ANALYZE, BUFFERS) EXECUTE availability(
  md5('labqueue:resource:100')::uuid,
  tstzrange(TIMESTAMPTZ '2026-06-01 00:00:00+00', TIMESTAMPTZ '2026-06-08 00:00:00+00', '[)'));

\echo ''
\echo '=== EXPLAIN — EF Core statement, generic plan ==='
SET plan_cache_mode = force_generic_plan;
EXPLAIN (ANALYZE, BUFFERS) EXECUTE availability(
  md5('labqueue:resource:100')::uuid,
  tstzrange(TIMESTAMPTZ '2026-06-01 00:00:00+00', TIMESTAMPTZ '2026-06-08 00:00:00+00', '[)'));

RESET plan_cache_mode;
DEALLOCATE availability;
