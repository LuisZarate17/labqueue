-- labqueue seed, part 2 of 2: reservations and maintenance windows.
--
--   docker compose exec -T db psql -U labqueue -d labqueue -f /db/seed/02_reservations.sql
--
-- The timeline is anchored to fixed literals rather than now(), so reloading on a
-- different day produces identical rows. Two years of 4-hour slots gives
-- 730 x 6 = 4,380 candidate slots per resource.
--
-- Confirmed reservations cannot overlap one another *by construction*: each one
-- is anchored at the start of a distinct grid slot and is never longer than the
-- slot, so containment in disjoint slots makes the rows disjoint. Nothing here
-- relies on a collision check.

\set ON_ERROR_STOP on
\timing on

BEGIN;

TRUNCATE reservations, maintenance_windows;

CREATE TEMP TABLE slot_grid ON COMMIT DROP AS
SELECT
  r.id                                                                            AS resource_id,
  r.code,
  s.slot,
  TIMESTAMPTZ '2025-09-01 00:00:00+00' + (s.slot * interval '4 hours')            AS slot_start,
  ('x' || substr(md5('keep:' || r.code || ':' || s.slot), 1, 7))::bit(28)::int % 10000 AS keep_roll,
  -- Per-resource keep rate on a power curve from 0.42 to 0.80: popular instruments
  -- book more, but the least-used resource still carries ~1,840 reservations.
  (10000 * (0.42 + 0.38 * power((r.ord - 1)::numeric / 199, 1.5)))::int           AS keep_threshold,
  ('x' || substr(md5('dur:'  || r.code || ':' || s.slot), 1, 7))::bit(28)::int % 3 AS dur_pick,
  ('x' || substr(md5('usr:'  || r.code || ':' || s.slot), 1, 7))::bit(28)::int     AS usr_roll,
  ('x' || substr(md5('mnt:'  || r.code || ':' || s.slot), 1, 7))::bit(28)::int     AS maint_roll
FROM (SELECT id, code, row_number() OVER (ORDER BY code) AS ord FROM resources) r
CROSS JOIN generate_series(0, 4379) AS s(slot);

-- ---------------------------------------------------------------- confirmed
INSERT INTO reservations (id, resource_id, user_id, during, status, created_at)
SELECT
  md5('labqueue:reservation:' || g.code || ':' || g.slot)::uuid,
  g.resource_id,
  u.id,
  tstzrange(
    g.slot_start,
    g.slot_start + (ARRAY[interval '1 hour', interval '2 hours', interval '4 hours'])[g.dur_pick + 1],
    '[)'),
  'confirmed',
  g.slot_start - interval '14 days'
FROM slot_grid g
JOIN (SELECT id, row_number() OVER (ORDER BY created_at) AS n FROM users) u
  ON u.n = (g.usr_roll % 300) + 1
WHERE g.keep_roll < g.keep_threshold;

-- ---------------------------------------------------------------- cancelled
-- Cancelled rows deliberately reuse a confirmed row's exact window. They are the
-- reason the eventual overlap constraint has to be partial on status: a total
-- constraint could not be created while these exist.
INSERT INTO reservations (id, resource_id, user_id, during, status, created_at, cancelled_at)
SELECT
  md5('labqueue:cancelled:' || r.id::text)::uuid,
  r.resource_id,
  r.user_id,
  r.during,
  'cancelled',
  lower(r.during) - interval '21 days',
  lower(r.during) - interval '3 days'
FROM reservations r
WHERE r.status = 'confirmed'
  AND ('x' || substr(md5('cancel:' || r.id::text), 1, 7))::bit(28)::int % 50 = 0;

-- ---------------------------------------------------------------- maintenance
-- Placed only in slots the confirmed pass dropped, so a maintenance window never
-- collides with a confirmed reservation. It occupies the whole slot; adjacent
-- slots abut but do not overlap, because every range is [).
INSERT INTO maintenance_windows (id, resource_id, during, reason, created_at)
SELECT
  md5('labqueue:maintenance:' || g.code || ':' || g.slot)::uuid,
  g.resource_id,
  tstzrange(g.slot_start, g.slot_start + interval '4 hours', '[)'),
  (ARRAY[
    'Scheduled preventive maintenance',
    'Calibration',
    'Vendor service visit',
    'Firmware upgrade',
    'Detector replacement'
  ])[(g.maint_roll % 5) + 1],
  g.slot_start - interval '30 days'
FROM slot_grid g
WHERE g.keep_roll >= g.keep_threshold
  AND g.maint_roll % 500 = 0;

COMMIT;

-- Planner statistics are part of the seed: an EXPLAIN taken against a table with
-- no statistics is not a measurement of anything.
VACUUM ANALYZE reservations;
VACUUM ANALYZE maintenance_windows;

\echo ''
\echo '--- reservations loaded ---'
SELECT
  (SELECT count(*) FROM reservations WHERE status = 'confirmed') AS confirmed,
  (SELECT count(*) FROM reservations WHERE status = 'cancelled') AS cancelled,
  (SELECT count(*) FROM maintenance_windows)                     AS maintenance_windows,
  pg_size_pretty(pg_total_relation_size('reservations'))         AS reservations_size;
