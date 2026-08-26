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

## Status

| Phase | |
|---|---|
| 01 — Domain model & migrations | ✅ |
| 02 — API & auth | ✅ |
| 03 — Tests & CI | ⬜ |
| 04 — Containerize & deploy | ⬜ |
| 05 — Observability | ⬜ |
| 06 — Load test & findings | ⬜ |
| 07 — Write-up | ⬜ |

## License

MIT
