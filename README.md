# labqueue

> Lab equipment reservation API — book instruments for a window of time, with
> certification gating and maintenance windows.

**🚧 In development.** Building in phases; this README fills in as each one closes.

![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET%2010-512BD4?style=flat&logo=dotnet&logoColor=white)
![PostgreSQL 17](https://img.shields.io/badge/PostgreSQL%2017-4169E1?style=flat&logo=postgresql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=flat&logo=docker&logoColor=white)
![k6](https://img.shields.io/badge/k6-7D64FF?style=flat&logo=k6&logoColor=white)
![Grafana](https://img.shields.io/badge/Grafana-F46800?style=flat&logo=grafana&logoColor=white)

## What this is

An ASP.NET Core reservation API over Postgres, used as the vehicle for two measured
database findings — a concurrency bug in the booking path and a missing index on the
overlap query. Each is measured before and after the fix, with load-test numbers and
query plans committed alongside the code.


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

## Status

| Phase | |
|---|---|
| 01 — Domain model & migrations | ✅ |
| 02 — API & auth | ✅ |
| 03 — Tests & CI | ✅ |
| 04 — Containerize & deploy | ✅ |
| 05 — Observability | ✅ |
| 06 — Load test & findings | ⬜ |
| 07 — Write-up | ⬜ |

## License

MIT
