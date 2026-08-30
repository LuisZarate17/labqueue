// Finding B — the availability read path.
//
// GET /resources/{id}/availability?from=&to= runs the overlap query:
//
//   SELECT * FROM reservations
//   WHERE resource_id = $1 AND status = 'confirmed' AND during && $2;
//
// Run identically before and after the GiST index lands. The only thing that may differ
// between runs is the schema.
//
//   docker run --rm -i --network labqueue_default \
//     -v "$PWD/loadtest:/scripts" -v "$PWD/docs/findings:/out" \
//     -e BASE_URL=http://api:8080 \
//     grafana/k6 run /scripts/availability.js --summary-export=/out/NAME.json

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://api:8080';

// The seed spans 2025-09-01 .. 2027-09-01. Queries land inside it, one week wide.
const SEED_START = Date.UTC(2025, 8, 1);
const SEED_END = Date.UTC(2027, 8, 1);
const WEEK = 7 * 24 * 60 * 60 * 1000;

export const availability = new Trend('availability_duration', true);

export const options = {
  scenarios: {
    read: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '30s', target: 50 },
        { duration: '2m', target: 50 },
        { duration: '30s', target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  // Recorded, deliberately not gating. This run measures; it does not pass or fail.
  thresholds: {
    availability_duration: ['p(50)<10000', 'p(95)<10000', 'p(99)<10000'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

// One login for the whole run. Password hashing is PBKDF2 at 100k iterations — tens of
// milliseconds — so authenticating per request would measure the hash, not the query.
export function setup() {
  const creds = {
    email: `loadtest-${Date.now()}@labqueue.test`,
    password: 'loadtest-labqueue-2026',
    displayName: 'Load Test',
  };

  const reg = http.post(`${BASE}/auth/register`, JSON.stringify(creds), {
    headers: { 'Content-Type': 'application/json' },
  });
  if (reg.status !== 201 && reg.status !== 409) {
    throw new Error(`register failed: ${reg.status} ${reg.body}`);
  }

  const login = http.post(
    `${BASE}/auth/login`,
    JSON.stringify({ email: creds.email, password: creds.password }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  if (login.status !== 200) {
    throw new Error(`login failed: ${login.status} ${login.body}`);
  }
  const token = login.json('token');

  // Every resource, so iterations spread across all 200 rather than heating one.
  const list = http.get(`${BASE}/resources?take=200`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (list.status !== 200) {
    throw new Error(`resource list failed: ${list.status} ${list.body}`);
  }

  const ids = list
    .json()
    .filter((r) => r.status === 'active')
    .map((r) => r.id);
  if (ids.length < 100) {
    throw new Error(`expected ~200 active resources, got ${ids.length} — is the seed loaded?`);
  }

  return { token, ids };
}

export default function (data) {
  // Random resource and random week. A fixed resource would sit in cache and understate
  // the cost of the scan the index is supposed to remove.
  const id = data.ids[Math.floor(Math.random() * data.ids.length)];
  const from = SEED_START + Math.random() * (SEED_END - SEED_START - WEEK);
  const to = from + WEEK;

  const url =
    `${BASE}/resources/${id}/availability` +
    `?from=${encodeURIComponent(new Date(from).toISOString())}` +
    `&to=${encodeURIComponent(new Date(to).toISOString())}`;

  const res = http.get(url, {
    headers: { Authorization: `Bearer ${data.token}` },
    tags: { name: 'availability' },
  });

  availability.add(res.timings.duration);
  check(res, { 'availability 200': (r) => r.status === 200 });
}
