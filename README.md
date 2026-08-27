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
cp .env.example .env          # then set Jwt__Key to 32+ bytes
docker compose up -d --build
./scripts/db-migrate.ps1 -Target local
```

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
| 04 — Containerize & deploy | 🚧 |
| 05 — Observability | 🚧 |
| 06 — Load test & findings | ⬜ |
| 07 — Write-up | ⬜ |

## License

MIT
