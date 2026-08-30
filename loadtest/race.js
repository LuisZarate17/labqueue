// Finding A — the check-then-act race in the booking path.
//
// ReservationService.BookAsync SELECTs for an overlapping reservation and then INSERTs if
// it found none, with no transaction and no lock. Under READ COMMITTED, concurrent callers
// all see an empty result and all insert.
//
// Fifty VUs, one iteration each, all posting the same resource and the same window. k6
// starts VUs near-simultaneously; it has no barrier primitive, so this is load shape and
// telemetry rather than the proof. The proof is the xUnit Barrier test in
// tests/LabQueue.Tests/ConcurrentBookingTests.cs. What this script produces that the unit
// test cannot is a spike on reservations.conflicts.total in Grafana, and double-booked
// rows in the benchmark database.
//
//   docker run --rm -i --network labqueue_default \
//     -v "$PWD/loadtest:/scripts" -v "$PWD/docs/findings:/out" \
//     -e BASE_URL=http://api:8080 \
//     grafana/k6 run /scripts/race.js --summary-export=/out/NAME.json

import http from 'k6/http';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://api:8080';
const VUS = Number(__ENV.VUS || 50);

export const created = new Counter('bookings_created');
export const conflicted = new Counter('bookings_conflicted');
export const other = new Counter('bookings_other');

export const options = {
  scenarios: {
    race: {
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: 1,
      maxDuration: '1m',
    },
  },
};

// A window past the end of the seed (2025-09-01 .. 2027-09-01), shifted by the current
// minute so every run gets a window nothing has booked. Reusing a window would hand every
// VU a legitimate 409, which looks exactly like the fix working.
function freshWindow() {
  const offsetHours = Math.floor(Date.now() / 60000) % 20000;
  const from = Date.UTC(2028, 0, 1) + offsetHours * 3600 * 1000;
  return { from: new Date(from).toISOString(), to: new Date(from + 2 * 3600 * 1000).toISOString() };
}

export function setup() {
  const creds = {
    email: `race-${Date.now()}@labqueue.test`,
    password: 'race-labqueue-2026',
    displayName: 'Race Test',
  };

  const reg = http.post(`${BASE}/auth/register`, JSON.stringify(creds), {
    headers: { 'Content-Type': 'application/json' },
  });
  if (reg.status !== 201) throw new Error(`register failed: ${reg.status} ${reg.body}`);

  const login = http.post(
    `${BASE}/auth/login`,
    JSON.stringify({ email: creds.email, password: creds.password }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  if (login.status !== 200) throw new Error(`login failed: ${login.status} ${login.body}`);
  const token = login.json('token');

  // An active resource with no certification gate, so rule 3 cannot mask the result.
  const list = http.get(`${BASE}/resources?take=200`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const resource = list.json().find((r) => r.status === 'active' && !r.requiredCertification);
  if (!resource) throw new Error('no ungated active resource found — is the seed loaded?');

  const win = freshWindow();

  // One token for all fifty VUs. PBKDF2 at 100k iterations is tens of milliseconds; logging
  // in per VU would stagger the requests by far more than the race window is wide.
  console.log(`race target  resource=${resource.id}  from=${win.from}  to=${win.to}`);
  return { token, resourceId: resource.id, ...win };
}

export default function (data) {
  const res = http.post(
    `${BASE}/reservations`,
    JSON.stringify({ resourceId: data.resourceId, from: data.from, to: data.to }),
    {
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${data.token}` },
      tags: { name: 'book' },
    },
  );

  if (res.status === 201) created.add(1);
  else if (res.status === 409) conflicted.add(1);
  else {
    other.add(1);
    console.error(`unexpected ${res.status}: ${res.body}`);
  }
}

export function teardown(data) {
  console.log(`race done   resource=${data.resourceId}  window=${data.from} .. ${data.to}`);
}
