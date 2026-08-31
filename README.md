# labqueue

> Lab equipment reservation API — book instruments for a window of time, with
> certification gating and maintenance windows.

[![ci](https://github.com/LuisZarate17/labqueue/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/LuisZarate17/labqueue/actions/workflows/ci.yml)
![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET%2010-512BD4?style=flat&logo=dotnet&logoColor=white)
![PostgreSQL 17](https://img.shields.io/badge/PostgreSQL%2017-4169E1?style=flat&logo=postgresql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=flat&logo=docker&logoColor=white)
![k6](https://img.shields.io/badge/k6-7D64FF?style=flat&logo=k6&logoColor=white)
![Grafana](https://img.shields.io/badge/Grafana-F46800?style=flat&logo=grafana&logoColor=white)

An ASP.NET Core reservation API over Postgres, used as the vehicle for two measured
database findings. Each was measured, then fixed, then measured again, with load-test
numbers and query plans committed alongside the code.

## What was found

| | before | after | |
|---|---|---|---|
| **A booking race.** 50 simultaneous bookings for one slot | **50 confirmed** | **1 confirmed** | 1,225 overlapping pairs → 0 |
| **An unindexed range predicate.** Availability query throughput | 3,423 req/s | 7,282 req/s | 2.13× |
| — p95 latency | 21.78 ms | 9.91 ms | 2.20× |
| — query execution time | 0.861 ms | 0.032 ms | 26.9× |

**Finding A** — the booking path selected for an overlapping reservation and then inserted
if it found none, with nothing holding the gap. Under `READ COMMITTED`, the Postgres
default and what EF Core gives you, every concurrent caller read the same empty result and
every one of them inserted. Not occasionally two: *every* request won. Fixed with a GiST
exclusion constraint, partial on `status = 'confirmed'` so cancelling frees the slot.

**Finding B** — the availability query filters on `resource_id` **and** on whether a
reservation overlaps a window. EF Core's foreign-key index covers the first half only, so
the plan found all 2,454 reservations for the resource and discarded 2,431 of them in a
heap filter to return 23. Fixed with a GiST index over `(resource_id, during)`.

The query got 27× faster; the HTTP request around it got 2.1×. JWT validation, EF
materialisation and serialisation were never the index's to fix, so **2.1× is the honest
headline** — see [Limitations](#limitations).

Method, query plans, raw k6 output and the reasoning: **[`docs/findings/`](docs/findings/README.md)**.
Why each fix was chosen over the alternatives: **[`DECISIONS.md`](DECISIONS.md)**.

## Evidence

Fifty concurrent bookings, one instrument, one two-hour window — before the fix:

```
bookings_created...............: 50    234.78701/s
bookings_conflicted............: 0
```

```sql
SELECT count(*) FROM reservations
WHERE resource_id = '5eb705d4…' AND status='confirmed'
  AND during && tstzrange('2028-03-31T15:00Z','2028-03-31T17:00Z','[)');
--  50
```

The identical script after the exclusion constraint landed:

```
bookings_created...............: 1      0.975135/s
bookings_conflicted............: 49     47.781634/s
```

Overlapping confirmed pairs across the whole table: **0**. The concurrency test that was
committed skipped in phase 03 — recorded failing 5/5 with 29 / 41 / 48 / 48 / 46 confirmed
rows where one was expected — is now un-skipped and green in CI.

And the availability query, same seed and same script, before and after the index:

```
before  avg=12.06ms med=11.48ms p(95)=21.78ms p(99)=27.59ms
after   avg=5.62ms  med=5.33ms  p(95)=9.91ms  p(99)=13.64ms
```

## Live demo

**https://labqueue-api.onrender.com**

```
email     demo@labqueue.dev
password  demo-labqueue-2026
```

`GET /` prints these plus a short how-to. The account is a **member**, not an admin —
admin routes can create maintenance windows, and a public admin login would let any
visitor block every resource for everyone after them.

**Dashboard:** [labqueue-api (public)](https://microcactus109.grafana.net/public-dashboards/25963f6e626440e0a4edd6664a40c038)
— request rate, error rate, latency percentiles, database query duration, and
`reservations.conflicts.total`. It reads empty unless someone is actively using the demo,
which is most of the time; the numbers that matter are committed under `docs/findings/`.

> **Cold start: about 22 seconds.** Both tiers sleep when idle — Render stops the container
> after 15 minutes, Neon scales compute to zero after 5. The first request wakes both, and
> measured after 20 minutes idle it took **22.2s** to first byte; connect and TLS were 0.05s
> and 0.07s of that, so the wait is startup, not network. Warm requests land in 75–130ms.

Hosted on Neon (database) and Render (app), both free tier. Deliberately **not** Render's
free Postgres, which is deleted at day 30 without warning.

## Architecture

Two projects plus tests. No deeper layering — onion-architecting seven phases of work is a
smell reviewers notice.

```
src/LabQueue.Api     minimal API endpoints, JWT auth, validation, OpenTelemetry
src/LabQueue.Core    entities, DbContext, migrations, ReservationService
tests/LabQueue.Tests xUnit + Testcontainers — 28 tests against real Postgres
```

Three endpoint groups — `/auth`, `/resources`, `/reservations` — plus admin routes for
creating resources, scheduling maintenance windows and granting certifications. Roles are
`member` and `admin`. Every error path returns Problem Details; Serilog logs are structured
from the first commit and carry a request id.

Reservations and maintenance windows each store their span as a single **`tstzrange`**
column rather than a `from`/`to` pair, which is what lets `&&` do overlap detection in the
index and in the constraint. All ranges are normalised to `[)` bounds by a `CHECK`
constraint rather than by application code, because `tstzrange` is continuous and Postgres
will not normalise it for you — one `[]`-bounded row silently changes what `&&` means at
the boundary.

Booking enforces five rules in order: the resource exists and is active → the window is
well-formed → the caller holds the required unexpired certification → no maintenance
overlap → no confirmed-reservation overlap. The last one is enforced by the database.

## Stack

| | |
|---|---|
| Runtime | .NET 10 (LTS), Minimal APIs |
| Database | PostgreSQL 17 — `tstzrange`, `btree_gist`, GiST, exclusion constraints |
| Data access | EF Core (Npgsql), with schema-level work in raw-SQL migrations |
| Tests | xUnit + Testcontainers (real Postgres, not a mock) |
| CI | GitHub Actions |
| Load testing | k6, run locally against docker-compose |
| Observability | OpenTelemetry → Grafana Cloud |

## Running it locally

Everything runs in containers. The API image is the same one that gets deployed.

```bash
cp .env.example .env             # then set Jwt__Key to 32+ bytes
docker compose up -d db          # database first
./scripts/db-migrate.ps1 -Target local
docker compose up -d --build api
```

Migrations are applied out of band rather than at startup, so the database comes up
first. Starting the API against an empty database is not fatal, but it is not useful
either — with `Demo__Seed=true` it will tell you to run the migration script.

The API is on `http://localhost:5140`; `/` prints what you need to start and `/health`
answers `{"status":"ok"}`. Set `Demo__Seed=true` in `.env` and the API seeds a small demo
dataset at boot — six resources, a maintenance window, a few upcoming bookings.

For the 500k-row benchmark dataset used by the load tests:

```bash
docker compose exec -T db psql -U labqueue -d labqueue -f /db/seed/01_reference.sql
docker compose exec -T db psql -U labqueue -d labqueue -f /db/seed/02_reservations.sql
```

Then either load test, against the compose network:

```bash
docker run --rm -i --network labqueue_default \
  -v "$PWD/loadtest:/scripts" -e BASE_URL=http://api:8080 \
  grafana/k6 run /scripts/availability.js      # or race.js
```

`scripts/gate02.sh` runs 24 HTTP checks against a running instance, and
`scripts/dev-test.ps1` runs the xUnit suite against a real Postgres via Testcontainers.

## Why the load tests do not run against the hosted instance

Free tiers throttle. Neon scales compute to zero after five minutes idle and free web
services spin down after fifteen. Point k6 at either and the P99 you measure is their rate
limiter, not your query plan.

So the two jobs are split. The hosted URL exists so a reviewer can click something real.
The measurements happen locally against docker-compose on the 500k-row seed, on hardware
with no noisy neighbours. Both environments export telemetry to the same Grafana stack,
distinguished by a `deployment.environment` resource attribute, so the local run is still
visible on the same dashboard as the hosted one.

## Limitations

**The maintenance-window race is still open.** Exclusion constraints are single-table.
Booking rule 4 compares `reservations.during` against `maintenance_windows.during`, across
two tables, so no exclusion constraint can enforce it. Two concurrent requests can still
book either side of a maintenance window being created. Closing it needs a trigger or
`SERIALIZABLE` isolation, and this project does neither — the reservation-vs-reservation
race is the one that was measured and fixed.

**The headline number is the smaller one.** Finding B improved the query 26.9× and the
request 2.13×. The difference is everything in a request that was never the index's fault:
JWT validation, model binding, EF materialisation, JSON serialisation, the network hop. At
this seed size the overlap query was a large part of a request but never all of it. Quoting
27× as a user-visible improvement would be wrong.

**The measurements are single-machine.** One Ryzen 7 9700X, Docker Desktop on WSL2, no
network between k6 and the API beyond the compose bridge. The relative before/after holds;
the absolute throughput would not survive a real network or a shared host.

**Cold starts on the demo.** ~22 seconds after idle, on both tiers. Fine for a portfolio
link, not fine for anything real.

## Status

| Phase | |
|---|---|
| 01 — Domain model & migrations | ✅ |
| 02 — API & auth | ✅ |
| 03 — Tests & CI | ✅ |
| 04 — Containerize & deploy | ✅ |
| 05 — Observability | ✅ |
| 06 — Load test & findings | ✅ |
| 07 — Write-up | ✅ |

## License

MIT
