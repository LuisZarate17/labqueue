# Decisions

Why this project is shaped the way it is. The first record is the one that justifies the
rest; everything after it is short.

---

## 1. Concurrency control: exclusion constraint, not locks or isolation

**Context.** `POST /reservations` checked for an overlapping confirmed reservation and then
inserted if it found none. Under `READ COMMITTED` — the Postgres default, and what EF Core
gives you — concurrent callers all read the same empty result and all insert. Measured at
50 simultaneous requests for one slot: 50 confirmed reservations, 1,225 overlapping pairs.

Three ways to close it.

### Advisory locks — rejected

`pg_advisory_xact_lock(hashtext(resource_id))` around the check and the insert serialises
callers per resource, and it works.

But correctness then lives in application code. Every write path that touches
`reservations` has to remember to take the lock, and the one that forgets reintroduces the
bug silently — no error, no failing test, just occasional double bookings under load. A
bulk import, an admin tool, a future "move this reservation" endpoint, a `psql` session
during an incident: any of them bypasses it. The guarantee is only as good as the
discipline of everyone who writes against the table from now on.

### `SERIALIZABLE` isolation — rejected

Correct, and it needs no application logic to be correct: Postgres detects the conflict and
aborts one transaction.

Two costs. Every caller has to handle `40001` and retry, so the complexity does not go away
— it moves into every endpoint rather than living in one place, and a caller that forgets to
retry turns a race into a user-visible 500. And it prices the whole application for one
path: `SERIALIZABLE` adds predicate-locking overhead to every transaction, including the
reads that have nothing to do with booking. Paying that globally to fix one write path is a
bad trade at this scale.

### Exclusion constraint — chosen

```sql
ALTER TABLE reservations
  ADD CONSTRAINT reservations_no_overlap
  EXCLUDE USING gist (resource_id WITH =, during WITH &&)
  WHERE (status = 'confirmed');
```

The database enforces it. **No write path can bypass it** — not a new endpoint, not a bulk
import, not a `psql` session at 2am. The guarantee is a property of the schema rather than
of anyone's memory, which is the whole difference from advisory locks.

It costs one GiST index, and the redundancy analysis then reclaimed most of that: the
constraint's own index made the standalone `ix_reservations_resource_during` unnecessary,
65 MB of the 130 MB, so the net cost against the already-indexed schema was one index, not
two.

The application still checks before inserting. That is not redundant — it answers the
uncontended case, which is nearly all of them, without a round trip to a failed insert and
with a better error message. It catches `23P01` and returns the same `409` for the
contended case. The check is an optimisation; the constraint is the guarantee.

**Known boundary.** Exclusion constraints are single-table, so this closes
reservation-vs-reservation only. Booking rule 4 compares reservations against
`maintenance_windows` across two tables, and that race is still open. Closing it needs a
trigger or `SERIALIZABLE`, and this project does neither. See README → Limitations.

---

## 2. The constraint is partial on `status = 'confirmed'`

A total constraint would make cancelled reservations keep blocking their slot forever.
Partial on `confirmed` means cancelling frees it, which is the behaviour the API already
promised and the test suite already asserted (`cancel, then rebook the same slot`).

It also means the seed can carry cancelled rows that overlap confirmed ones without the
constraint refusing to build — which is a useful check that the predicate is right.

---

## 3. Keep EF Core's foreign-key index

An earlier build removed `ForeignKeyIndexConvention`, which suppressed the btree EF Core
creates on `reservations.resource_id`. The availability query then did a sequential scan
over 500k rows, and the GiST index looked ~579× better.

That was reverted before anything was measured. A reservation API written with EF Core
*has* that index — you get it without asking. Suppressing it makes the "before" number a
property of the measurement setup rather than of the code, and folds "you forgot an index
entirely" into what is meant to be a claim about **range** indexing. Two different findings
were being reported as one, and the interesting one was the smaller.

So the baseline is the schema EF gives you by default, and Finding B is measured from
there: 2.13× on throughput, not 579× on a number nobody would have shipped.

---

## 4. Benchmark locally, deploy to free tiers

Neon scales compute to zero after five minutes idle; Render free services spin down after
fifteen. Point k6 at either and the P99 you measure is their rate limiter, not your query
plan.

So the hosted URL exists to be clicked, and the measurements happen locally against
docker-compose on the 500k-row seed. Both environments export to the same Grafana stack,
split by a `deployment.environment` resource attribute, so the local benchmark is still
visible on the same dashboard as the demo.

The cost is that the absolute numbers are single-machine and would not survive a real
network. The relative before/after — same seed, same script, same hardware, one warm-up
discarded, full teardown between runs — is what the findings actually claim.

---

## 5. EF Core, with schema-level work in raw-SQL migrations

EF Core for the model and CRUD. `tstzrange` maps to `NpgsqlRange<DateTime>` natively and
`btree_gist` is declared through `HasPostgresExtension`, but exclusion constraints have no
fluent API, so they ship as `migrationBuilder.Sql(...)`.

That turns out to be convenient rather than awkward: the one thing that needs raw SQL is
exactly the thing a reviewer wants to read as SQL, and each finding's fix is a single
readable migration rather than a diff in a model snapshot.

Every schema change is a migration — never hand-run `psql`. Testcontainers builds a fresh
database from migrations on each CI run, so a fix applied by hand to a local volume passes
on the developer's machine and fails in Actions.

---

## 6. The smaller records

**.NET 10 over 8.** .NET 8 reaches end of life on 10 November 2026. A portfolio repo read
after that date should not be built on an unsupported runtime. .NET 10 is LTS into 2028.

**Postgres 17 pinned in both environments.** Query plans are version-sensitive and this
project's entire deliverable is a claim about query plans. If the hosted provider offered
something other than 17, the *local* environment would change to match, never the reverse.

**Testcontainers over a mocked database.** A mock cannot exhibit a race condition. The
whole of Finding A is behaviour that only exists when a real Postgres serves two real
connections under `READ COMMITTED`. This is load-bearing, not a style preference.

**k6 for load shape, xUnit for proof.** k6 has no barrier primitive and VU startup staggers
by tens of milliseconds against a sub-millisecond race window, so a k6 run can produce zero
double-bookings and prove nothing either way. The proof is the xUnit test that releases
fifty in-process threads at a `Barrier`. k6 supplies realistic load and the telemetry spike.

**Neon for the database, Render for the app only.** Render's free Postgres is deleted at day
30 without warning. A portfolio link that dies is worse than no link. Neon's free tier is
permanent and supports `btree_gist`.

**The demo account is a member, not an admin.** Admin routes create maintenance windows. A
public admin login would let any visitor block every resource for everyone after them.

**Minimal APIs, not controllers.** Current ASP.NET idiom, and controllers add ceremony this
project does not need.

**Two dashboard JSON files.** Grafana's externally shared dashboards do not support template
variables, so the `$env` filter and the public link cannot coexist in one file. The
reasoning and the regeneration recipe are in
[`docs/dashboards/README.md`](docs/dashboards/README.md).

**A containerised build and test loop.** Windows Smart App Control is enforcing on the
development machine and blocks freshly built unsigned assemblies with `0x800711C7`. Builds,
`dotnet ef` and the test suite run inside the .NET SDK container instead. Turning Smart App
Control off is irreversible without reinstalling Windows, which is a poor trade for a faster
inner loop — and the container path pre-validates the Linux image that gets deployed.

---

<<<<<<< HEAD
## 7. The exclusion constraint needed a retry policy to be complete

**Context.** Section 1 chose an exclusion constraint and ended there, as though the write
path were finished. It was not. `reservations_no_overlap` closes the race correctly, but it
does not decide *how* Postgres resolves the contention it creates, and the error path only
handled one of the two answers.

Fifty simultaneous inserts for one slot mostly resolve the way section 1 describes: one
commits, forty-nine hit `23P01`, the application turns each into a `409`. Sometimes they do
not. Each inserter takes a speculative lock and waits on the transactions it conflicts with,
and with enough mutual waiters the wait-for graph forms a cycle. Postgres detects it after
`deadlock_timeout` and kills a victim with `40P01`. The remaining waiters can then form new
cycles, and it cascades — a captured run shows **99 deadlocks, zero exclusion violations,
every insert timed at exactly 1,002ms**, and not one reservation committed.

`BookAsync` caught `23P01` and nothing else, so every one of those became a `500`.

**This failure mode is named in this document already.** Section 1 rejected `SERIALIZABLE`
partly because "every caller has to handle `40001` and retry ... a caller that forgets to
retry turns a race into a user-visible 500". The exclusion constraint was chosen over it and
then did precisely that, one SQLSTATE over. The argument was right; it was applied to the
alternative and not to the choice.

**Why it stayed hidden.** The constraint landed with a concurrency test that proves it, and
that test was green. Deadlock is a timing accident — roughly one full-suite run in twenty on
the development machine, and never when the test runs alone, because a single class starts
the process fresh. It was first mistaken for test flakiness. It is not: the same 500 is
returned to a real caller under real contention, and it has been in the deployed API since
the constraint landed.

### `EnableRetryOnFailure` — tried, measured, rejected

The obvious fix is EF Core's own resiliency: add `EnableRetryOnFailure` to `UseNpgsql`, let
the victim retry, done. It was implemented that way first. It made the failure worse, and
the reason is a granularity mismatch worth recording.

**EF Core's execution strategy retries `SaveChangesAsync`, not the operation around it.** So
every deadlock victim goes straight back to the `INSERT`, without re-reading, and re-contends
on the same constraint. Fifty of them do that together, on a short backoff, repeatedly. The
lock queue stops draining.

Measured, at five retries and a 250ms cap, against the same test:

| | without retry | with `EnableRetryOnFailure` |
|---|---|---|
| failing statement | `40P01` after 1,002ms (`deadlock_timeout`) | timeout after **30,008ms** (`CommandTimeout`) |
| deadlocks in the run | 99 | 14 |
| reservations committed | 0 | 0 |
| test duration | 30s | **1m 40s**, ending in a client-side timeout |

A fast deadlock storm became a slow lock convoy. Tuning the retry count and backoff moves
the numbers around; it does not fix the shape, because the retry is aimed at the wrong unit
of work.

### Retry the booking, not the save

`BookAsync` catches `40P01` itself and retries a bounded number of times, and each attempt
**re-reads before it re-inserts**. That single difference is what makes it work: by the time
a victim wakes, the winner has usually committed, so the re-read finds the overlapping row
and the victim returns `409` *without touching the constraint again*. The herd drains
instead of re-forming. Only a victim that finds the window still free goes back to the
`INSERT`. Delays are jittered so fifty victims do not wake in step.

It is worth being precise about why retrying at all is necessary. Mapping `40P01` to a
status code without retrying would also have removed the `500` — and would have been worse
than useless: in a cascade, all fifty callers would be told the booking failed while the slot
sat empty and nothing was booked.

This also keeps the blast radius at one method. A retry policy on the context would have
changed the failure behaviour of every write path in the application to fix one of them.

### A deadlock is not a conflict

The residual case — a deadlock that survives the retries — returns **`503` with a
`Retry-After`**, not `409`, and increments its own counter rather than
`reservations.conflicts.total`.

Both halves of that are the same point. A deadlock victim is killed *before* it establishes
whether the window was taken; no overlapping row was ever observed. Returning `409
Reservation conflict` would assert a conflict that was never measured, and folding it into
the conflict counter would hide it inside a number the README quotes as evidence that the
constraint works. `reservations.deadlocks.total` counts the times the database could not
answer the question, which is a different fact worth seeing on its own.

The catch matches on the SQLSTATE anywhere in the exception chain rather than on a wrapper
type, and deliberately not on a broad exception filter. Npgsql classifies deadlock as
transient, so EF Core never surfaces it as a bare `DbUpdateException`: it arrives inside
`RetryLimitExceededException` when a retrying strategy runs out of attempts, and inside
`InvalidOperationException` when no retry policy is configured. Adding `40P01` to the
existing `23P01` clause would not have caught it — the clause is typed on
`DbUpdateException`, and a deadlock is never one.

### What this cost

Nothing at the schema level: no migration, no change to the constraint, no change to the
five booking rules, and no change to any write path but this one. A bounded retry inside
`BookAsync`, one catch clause, one status code and one counter.

The constraint was right. Its failure handling was incomplete, the test that proved the
constraint was also the thing that found the gap, and the first fix attempted for it was
wrong in a way only the same test could show.
=======
## 8. Browsable docs, served in Production

**Context.** The gate for this project was "a stranger can complete a booking through the
live URL". That was true only for a stranger with a terminal: there was no OpenAPI document
and no UI, so `GET /` handed visitors a `howTo` string telling them to go write HTTP requests
by hand.

**Scalar over Swagger UI.** .NET 10 emits OpenAPI 3.1 by default. Swagger UI's 3.1 support
arrived late and is the historic weak spot of that stack, so choosing it would have meant
either trusting that support or pinning `OpenApiVersion` back to 3.0 and downgrading the
document to suit the viewer. Scalar consumes 3.1 natively. It also serves its bundle from
assets embedded in the package — with `DisableDefaultFonts()` the page renders with nothing
leaving the origin, which is what a free-tier deploy in front of an unknown reviewer's
network wants. MIT, no NuGet dependencies. ReDoc was ruled out for having no try-it-out,
which makes the 409 step impossible.

**No `IsDevelopment()` gate, and no configuration toggle.** The hosted deployment is the
only reason this API is public. Docs behind the template's environment check would work on
every developer machine and 404 on the one URL that matters, which is the same gap wearing
a disguise; a toggle is that failure with an extra step. The costs are named and accepted:
two more anonymous endpoints disclosing the route table and DTO shapes, which a public
repository already discloses; one lazy document generation per cold start, inside a wake
already dominated by Render and Neon; and docs traffic excluded from traces, as `/health`
already was.

**The bearer scheme is applied from a document transformer, not an operation transformer.**
An operation transformer reading endpoint metadata is the obvious shape and is what the
ASP.NET Core documentation shows. On .NET 10 an `OpenApiSecuritySchemeReference` constructed
there does not resolve and serialises as `"security": [{}]` — dotnet/aspnetcore#64524, closed
as a duplicate of microsoft/OpenAPI.NET#2300 and marked by design. The failure is silent in
the worst way: the page renders, the *Authorize* button appears, and no request ever carries
the header. A document transformer holds the real `OpenApiDocument`, so references built
there resolve.

Two smaller traps in the same code, both silent:

- Endpoint metadata is matched on `IAuthorizeData` / `IAllowAnonymous`, not on
  `AuthorizeAttribute` / `AllowAnonymousAttribute`. Every route here is configured fluently
  on a group, which contributes metadata implementing the interfaces rather than the
  attributes themselves.
- `ApiDescription.RelativePath` keeps route constraints (`resources/{id:guid}`) while the
  document keys paths without them (`/resources/{id}`). Matching the two raw skipped every
  parameterised route — four of the nine protected operations — and left them looking
  anonymous in the reference while still returning 401.

**Responses are described with metadata rather than typed results.** Every handler returns
`IResult`, so the generated document had request bodies and almost no responses: the 409 on
`POST /reservations` is the demo, and it was missing. `Produces` and `ProducesProblem`
annotations fix that without touching a single handler body or status code. Converting the
handlers to `Results<Created<T>, ProblemHttpResult, …>` would infer the same information,
but it rewrites the return type and switch expression of every handler for a documentation
benefit, and this project's booking path is the last place to take avoidable churn.

**Four tests, because every one of these failures is invisible locally.** The fixture runs
under `ASPNETCORE_ENVIRONMENT=Testing`, which is what makes "the document is served outside
Development" a real check rather than a tautology. They were verified by reintroducing the
route-constraint bug and confirming the suite fails and names all four affected operations.
>>>>>>> docs/browsable-api
