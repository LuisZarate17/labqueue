-- labqueue seed, part 1 of 2: reference data.
--
-- Every identifier is derived with md5() from a stable string, so a reload
-- produces byte-identical rows. Load-test scripts and captured query plans can
-- therefore cite a specific resource id and still be valid after a reseed.
--
--   docker compose exec -T db psql -U labqueue -d labqueue -f /seed/01_reference.sql

\set ON_ERROR_STOP on
\timing on

BEGIN;

TRUNCATE reservations, maintenance_windows, user_certifications, resources, users, certifications;

-- ---------------------------------------------------------------- certifications
INSERT INTO certifications (id, code, name, description)
SELECT md5('labqueue:certification:' || code)::uuid, code, name, description
FROM (VALUES
  ('BSL2',     'Biosafety Level 2',        'Handling of BSL-2 biological agents.'),
  ('LASER-3B', 'Class 3B Laser Operation', 'Operation of Class 3B laser systems.'),
  ('NMR-OP',   'NMR Operator',             'Independent operation of NMR spectrometers.'),
  ('RAD-A',    'Radiation Safety A',       'Work with sealed radioactive sources.'),
  ('CRYO',     'Cryogenics Handling',      'Safe handling of liquid nitrogen and helium.'),
  ('CLEANRM',  'Cleanroom Protocol',       'ISO-7 cleanroom gowning and protocol.')
) AS c(code, name, description);

-- ---------------------------------------------------------------- users
-- All seeded accounts share the password 'labqueue-dev-password'. This is local
-- demo data; the hosted deployment seeds its own admin from an environment
-- variable and none of these rows are loaded there.
INSERT INTO users (id, email, password_hash, display_name, role, created_at)
SELECT
  md5('labqueue:user:' || i)::uuid,
  CASE i WHEN 1 THEN 'admin@labqueue.test'
         WHEN 2 THEN 'ops@labqueue.test'
         ELSE 'researcher' || lpad(i::text, 3, '0') || '@labqueue.test' END,
  'AQAAAAIAAYagAAAAEM4aBcA/8g3n+2pI5BwLtVE/10dIyYfxcvzbyvvI6jrGjja0Ttw+0pGpIf6enolS5w==',
  CASE i WHEN 1 THEN 'Ada Admin'
         WHEN 2 THEN 'Operations Desk'
         ELSE 'Researcher ' || lpad(i::text, 3, '0') END,
  CASE WHEN i <= 2 THEN 'admin' ELSE 'member' END,
  TIMESTAMPTZ '2025-08-01 00:00:00+00' + (i || ' minutes')::interval
FROM generate_series(1, 300) AS g(i);

-- ---------------------------------------------------------------- resources
-- Four of the ten instrument families require a certification, so roughly 40%
-- of resources are gated. The last five are retired.
INSERT INTO resources (id, code, name, location, description, required_certification_id, status, created_at)
SELECT
  md5('labqueue:resource:' || i)::uuid,
  'RES-' || lpad(i::text, 3, '0'),
  f.family || ' ' || lpad(i::text, 3, '0'),
  'Building ' || chr(65 + (i % 5)) || ', Room ' || (100 + (i % 40))::text,
  f.description,
  c.id,
  CASE WHEN i > 195 THEN 'retired' ELSE 'active' END,
  TIMESTAMPTZ '2025-08-15 00:00:00+00' + (i || ' minutes')::interval
FROM generate_series(1, 200) AS g(i)
JOIN LATERAL (
  SELECT family, cert_code, description
  FROM (VALUES
    (0, 'Confocal Microscope',     'BSL2',     'Laser scanning confocal microscope with incubation stage.'),
    (1, 'Flow Cytometer',          'BSL2',     'Multi-parameter analyser, four-laser configuration.'),
    (2, 'NMR Spectrometer',        'NMR-OP',   '400 MHz benchtop NMR spectrometer.'),
    (3, 'Femtosecond Laser Bench', 'LASER-3B', 'Ti:sapphire ultrafast laser bench.'),
    (4, 'Cryostat',                NULL,       'Closed-cycle helium cryostat.'),
    (5, 'Centrifuge',              NULL,       'High-speed floor-standing centrifuge.'),
    (6, 'Plate Reader',            NULL,       'Multi-mode microplate reader.'),
    (7, 'PCR Thermocycler',        NULL,       'Gradient thermocycler, 96-well block.'),
    (8, 'Mass Spectrometer',       NULL,       'Quadrupole time-of-flight mass spectrometer.'),
    (9, 'Rheometer',               NULL,       'Rotational rheometer with temperature control.')
  ) AS v(k, family, cert_code, description)
  WHERE v.k = i % 10
) AS f ON TRUE
LEFT JOIN certifications c ON c.code = f.cert_code;

-- ---------------------------------------------------------------- grants
-- Roughly 55% of user/certification pairs are granted. About one in twelve is
-- already expired, so the booking path has an expired-certification case to hit.
-- The two admins hold everything, unexpired.
INSERT INTO user_certifications (user_id, certification_id, granted_at, expires_at)
SELECT
  u.id,
  c.id,
  TIMESTAMPTZ '2025-06-01 00:00:00+00' + ((u.n * 3 + c.m) || ' days')::interval,
  CASE
    WHEN u.n <= 2 THEN NULL
    WHEN ('x' || substr(md5('exp:' || u.n || ':' || c.m), 1, 7))::bit(28)::int % 12 = 0
      THEN TIMESTAMPTZ '2026-04-01 00:00:00+00'
    WHEN ('x' || substr(md5('exp:' || u.n || ':' || c.m), 1, 7))::bit(28)::int % 4 = 0
      THEN NULL
    ELSE TIMESTAMPTZ '2027-12-31 00:00:00+00'
  END
FROM (SELECT id, row_number() OVER (ORDER BY created_at) AS n FROM users) u
CROSS JOIN (SELECT id, row_number() OVER (ORDER BY code) AS m FROM certifications) c
WHERE u.n <= 2
   OR ('x' || substr(md5('grant:' || u.n || ':' || c.m), 1, 7))::bit(28)::int % 100 < 55;

COMMIT;

\echo ''
\echo '--- reference data loaded ---'
SELECT
  (SELECT count(*) FROM certifications)      AS certifications,
  (SELECT count(*) FROM users)               AS users,
  (SELECT count(*) FROM resources)           AS resources,
  (SELECT count(*) FROM resources WHERE required_certification_id IS NOT NULL) AS gated_resources,
  (SELECT count(*) FROM user_certifications) AS grants;
