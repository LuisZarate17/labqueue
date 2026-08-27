# Dashboards

Two files, same five panels, and the duplication is deliberate.

| File | Purpose |
|---|---|
| `labqueue-api.json` | The working dashboard. Carries an `$env` template variable over the `deployment_environment` label, so one dashboard shows either the hosted demo or the local benchmark rig. |
| `labqueue-api-public.json` | The variable-free copy that is shared externally. Every query has `deployment_environment="hosted"` written into it literally. |

## Why there are two

Grafana's externally shared dashboards **do not support template variables** — "Variables and
queries including variables are not supported" is an explicit documented limitation. So a
single dashboard cannot both carry the `$env` filter and load for a logged-out stranger.

The alternatives were worse. Dropping `$env` entirely would mean hand-editing panel queries to
watch local traffic, which is exactly the capability Phase 06 depends on. Publishing a snapshot
instead would freeze the data, and Grafana Cloud disables external snapshot publishing by
default, so the URL would ask strangers to log in — the failure mode the gate exists to catch.

## Regenerating the public copy

`labqueue-api-public.json` is derived, not maintained by hand. After changing
`labqueue-api.json`, regenerate it rather than editing both:

```bash
cd docs/dashboards
perl -0pe '
  s/deployment_environment=\\"\$env\\"/deployment_environment=\\"hosted\\"/g;
  s/"templating": \{.*?\n  \},\n/"templating": { "list": [] },\n/s;
  s/"title": "labqueue-api",/"title": "labqueue-api (public)",/;
  s/"uid": "labqueue-api",/"uid": "labqueue-api-public",/;
' labqueue-api.json > labqueue-api-public.json
```

Then confirm no query still references a variable:

```bash
node -e 'const d=require("./labqueue-api-public.json");
  d.panels.forEach(p=>p.targets.forEach(t=>{if(t.expr.includes("$env"))throw new Error(t.expr)}));
  console.log("clean:", d.templating.list.length, "variables");'
```

## Importing

Both files use the standard Grafana export shape with a `DS_PROMETHEUS` input, so importing
prompts for the Prometheus data source rather than hard-coding a stack-specific UID.

Dashboards → New → Import → Upload JSON file → pick the Grafana Cloud Prometheus source.

## Metric names

Confirmed against a local OpenTelemetry collector before the panels were written, rather than
assumed from the instrument names:

| Instrument | Series in Prometheus |
|---|---|
| `http.server.request.duration` (ASP.NET Core) | `http_server_request_duration_seconds_{bucket,count,sum}` |
| `db.client.operation.duration` (Npgsql) | `db_client_operation_duration_seconds_{bucket,count,sum}` |
| `reservations.conflicts.total` (application) | `reservations_conflicts_total` |

The counter arrives as `reservations_conflicts_total`, **not** `reservations_conflicts_total_total`
— the OTLP-to-Prometheus translation appends `_total` to monotonic counters only when the name
does not already end in it.

Panel 4 aggregates with `sum by (le)` rather than breaking out by connection pool. Npgsql tags
that metric with `db_client_connection_pool_name`, which contains the database host and username
(the password is stripped). Aggregating keeps it off a publicly shared dashboard.
