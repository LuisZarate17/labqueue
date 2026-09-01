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

### Retry, on the context

`EnableRetryOnFailure`, configured once on the `DbContext` rather than around the one call
site. Same reasoning that rejected advisory locks in section 1: correctness every write path
has to remember is correctness that will eventually be forgotten. The objection that sank
`SERIALIZABLE` — pricing the whole application for one path — does not apply, because a
retry policy costs nothing on requests that do not fail.

The retried insert runs against a settled table, so it resolves definitively: it either wins
the slot or loses it to `23P01` and takes the existing `409` path. That is what makes the
concurrency test green again, and it is worth being precise about why. Mapping `40P01` to
`409` without retrying would also have removed the `500`, and would have been worse than
useless: fifty callers would each be told the slot was taken while nothing was booked.

EF Core's execution strategy applies to every `SaveChangesAsync` in the application. That is
safe here because no code path opens an explicit transaction — a retrying strategy requires
user-initiated transactions to be wrapped in `CreateExecutionStrategy().ExecuteAsync(...)`,
and there are none to wrap.

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
five booking rules. The fix is a retry policy, one catch clause, one status code and one
counter. The constraint was right. Its failure handling was incomplete, and the test that
proved the constraint was also the thing that eventually found the gap.
