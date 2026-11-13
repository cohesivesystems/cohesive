# Reaction discovery: excluding terminal history

This SQL-only comparison isolates the discovery query shape before and after pending-instance filtering.
It is not an end-to-end recovery or target-throughput benchmark. The fixture copies root metadata to
10,000 terminal identities and one pending identity inside a rolled-back transaction; those synthetic
identities are not valid executable Process aggregates. Run only against an isolated PostgreSQL test
schema initialized by `PostgresProcessDurableStore.EnsureCreatedAsync` with at least one retained root.

PostgreSQL 17, local Docker, warm buffers, one exploratory run:

| Complete-pass discovery | Returned identities | Shared buffer hits | Execution time |
|---|---:|---:|---:|
| Previous all-history predicate | 10,001 | 589 | 1.688 ms |
| Pending predicate and composite index | 1 | 3 | 0.008 ms |

The query plans, not the single-run timings, are the useful evidence: the pending query uses an
index-only scan constrained by authority and terminal status. The all-history predicate scans and
sorts the retained identities. These queries omit page limits to compare one complete pass; production
uses bounded keyset pages. This excludes aggregate loads, JSON parsing, network overhead and target
calls, so it does not estimate total recovery latency or a production speedup.

`compare.sql` reproduces the measurement and rolls its fixture back. `query-plans.txt` retains the
observed plans. Regression tests separately prove that completed and rejected reactions cause zero
additional target execution or receipt-resolution calls on a subsequent background pass, and that
PostgreSQL maintains the terminal projection through commit, reconnection and lazy migration.
