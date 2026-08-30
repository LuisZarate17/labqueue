# Findings

Two measured database findings, each fixed only after it was measured. Every number below
is reproducible from a committed script against the committed seed.

| | |
|---|---|
| Hardware | Ryzen 7 9700X, 32 GB DDR5, Docker Desktop (WSL2) |
| Database | PostgreSQL 17.11, local docker-compose |
| Seed | 500,942 confirmed reservations, 200 resources, two years |
| Load tool | k6 on the compose network, hitting `api:8080` directly |

Measurements ran locally, not against the hosted demo. Free tiers throttle: Neon scales
compute to zero after five minutes idle and Render free services spin down after fifteen,
so a P99 measured there is their rate limiter rather than this query plan.

---

## Finding A — a check-then-act race in the booking path

`ReservationService.BookAsync` selected for an overlapping reservation and then inserted if
it found none, with no transaction and no lock between the two statements. Under
`READ COMMITTED` — the Postgres default, and what EF Core gives you — every concurrent
caller reads the same empty result and every one of them inserts.

**Fifty simultaneous bookings for one two-hour slot produced fifty confirmed reservations.**
Not occasionally two. Every request won, and 1,225 overlapping pairs existed afterwards.

| | before | after |
|---|---|---|
| `201 Created` | **50** | **1** |
| `409 Conflict` | 0 | 49 |
| confirmed rows in the window | 50 | 1 |
| overlapping pairs (whole table) | 1,225 | 0 |

**Fix** — a GiST exclusion constraint, partial on `status = 'confirmed'` so cancelling frees
the slot. The database refuses the second insert; no write path can bypass it, including a
`psql` session. The application catches `23P01` and returns the same `409` the pre-insert
check returns.

Detail: [finding-a-before.txt](finding-a-before.txt) · [finding-a-after.txt](finding-a-after.txt) ·
[finding-a-repro.txt](finding-a-repro.txt)

---

## Finding B — the range predicate was never indexed

The availability query filters on `resource_id` **and** on whether the reservation overlaps
a requested window. EF Core's foreign-key index covers the first half only, so the plan
found every reservation for the resource and then discarded almost all of them in a heap
filter — 2,454 rows read to return 23.

| | before (btree) | after (GiST) | |
|---|---|---|---|
| p50 | 11.48 ms | 5.33 ms | 2.15× |
| p95 | 21.78 ms | 9.91 ms | 2.20× |
| p99 | 27.59 ms | 13.64 ms | 2.02× |
| throughput | 3,423 req/s | 7,282 req/s | 2.13× |
| query execution | 0.861 ms | 0.032 ms | **26.9×** |
| buffers touched | 2,453 | 28 | 88× fewer |
| rows discarded by filter | 2,431 | 0 | |

Those two blocks disagree, and the disagreement is the honest part. The query got ~27×
faster; the HTTP request it sits inside got ~2.1× faster. The rest of a request — JWT
validation, model binding, EF materialisation, JSON serialisation, the network hop — was
never the index's to fix. **Doubling throughput on a one-line schema change is the
headline; 27× is true at the database and is not what a caller experiences.**

**Why the baseline is a btree and not a sequential scan.** An earlier build suppressed EF
Core's `ForeignKeyIndexConvention`, which removed the index EF creates on
`reservations.resource_id` and produced a sequential scan — and a far larger apparent
improvement. That was reverted before any of this was measured. Removing an index the ORM
would have created makes the "before" number a property of the measurement setup rather
than of the code, and folds "no index at all" into what is meant to be a claim about range
indexing.

Detail: [finding-b-before.txt](finding-b-before.txt) · [finding-b-after.txt](finding-b-after.txt) ·
[baseline-explain.txt](baseline-explain.txt)

---

## The redundant index

An exclusion constraint builds its own index. Once `reservations_no_overlap` existed, its
GiST index and `ix_reservations_resource_during` covered the same predicate — both partial
on `status = 'confirmed'`, **65 MB each**, so half of that 130 MB was paying for nothing on
disk and on every insert.

Dropped the standalone one and confirmed the query still plans as a bitmap index scan, now
on `reservations_no_overlap`, with the same index condition covering both halves of the
predicate: 23 rows, 27 buffers, 0.383 ms.

`IX_reservations_resource_id` is deliberately left in place — it is EF's foreign-key index,
it is not partial on status, and it serves lookups the constraint's index does not.

**This is also why the order could not be reversed.** Fixing A first would have created the
constraint's GiST index and silently fixed B at the same time, collapsing two independent
findings into one that could not be measured separately.

---

## Known limitation

Exclusion constraints are **single-table**. Booking rule 4 compares `reservations.during`
against `maintenance_windows.during`, so the reservation-vs-maintenance race is still open.
Closing it needs a trigger or `SERIALIZABLE` isolation, and this project does neither.
