# Reservation replay assurance

Run one canonical reservation transition against PostgreSQL, lose its acknowledgement after COMMIT, recreate the client and repository, and retry. Compare atomic receipt replay with a deliberately defective realization that re-evaluates the transition on every retry.

```sh
./eng/examples/replay-assurance/run-local.sh
```

Requires .NET 10 and a running Docker-compatible engine with the `docker` CLI. The script uses `postgres:17.10-alpine3.24`, creates a uniquely named container with temporary database storage and a random loopback-only port, runs the Assurance tests including the two native cases, then runs the demo. Its exit/signal trap removes only its own container. No Aspire host, CQRS worker, external services, or existing databases are used. Docker may download the named image if absent.

An optional argument chooses the artifact directory:

```sh
./eng/examples/replay-assurance/run-local.sh /tmp/reservation-evidence
```

Without an argument the runner creates a fresh directory under this example's `artifacts/` directory when launched by the script. Direct `dotnet run` uses an `artifacts/replay-assurance/` directory relative to the current working directory.

## Expected result

| Realization | Final stock | Committed stock updates | Receipts | Claim verdict |
| --- | --- | --- | --- | --- |
| Atomic receipt | 8 | 1 | 1 | Satisfied in the closed fixture scope |
| Unprotected retry | 6 | 2 | 0 | Violated |

Both start with ten available units and reserve two. The successful path calls the existing `CommitTransitionOperation` protocol with the same canonical request/commit. The first pool is disposed after the injected acknowledgement loss; retry creates a new data source, revalidates receipt storage, and constructs a new repository. It verifies that replay returns the original commit fingerprint.

The defective path uses ordinary repository writes without receipts and re-evaluates the same transition after the lost acknowledgement. It is an explicit test control, not a supported weakening of the production protocol. Both paths use the same claim and evaluator.

## What the observation establishes

A test-local PostgreSQL `AFTER UPDATE` trigger writes an audit row in the same transaction as every physical inventory update. Database-generated audit identities distinguish committed effects even when a caller never receives a response. Rolled-back writes and their audit rows disappear together; identity sequence gaps are not effects. The trigger records before/after stock, so tests check actual decrements independently of the returned replay disposition.

The fixture has exactly one logical operation, one selected inventory row, and no unrelated writers. It maps each committed audit effect to that exact operation occurrence. It does not infer effect counts from semantic versions, count receipt reads as new effects, or sort timestamps to manufacture a global order. The fixture's audit SQL is native test instrumentation; product schema, mapping, receipt validation, and execution reuse the existing PostgreSQL adapter mechanisms.

The fault is injected at the caller acknowledgement boundary after the repository returns from native COMMIT. This exercises lost responses and client reconstruction, not a PostgreSQL crash or network failure during COMMIT. The finite scope closes after all calls finish and the fixture-owned schema is removed. The evaluator reports no universal proof from these runs.

## Evidence and independence

Each realization writes:

- `*.evidence.json`: exact claim, transition, observation mapping, unique target run identity, tested binary fingerprint, server version, scope closure, and audit-derived observations.
- `*.audit.json`: committed update identities and stock changes.
- `*.txt`: stable verdict explanation.

Artifacts are created without overwriting existing files; use a fresh output directory for another run. The output never includes the connection string. `EffectUniquenessDocument.Restore` validates and re-evaluates the retained check. Audit files are supporting raw fixture output, not authenticated attestations or an additional claim authority.

The example references the existing `Orders.Model` project read-only to reuse `InventoryTransitions.Reserve`; it does not modify or run Aspire/CQRS composition. All additions live in this example and the Assurance tests. No storage runtime or infrastructure semantic changes are required.

## Run against an explicitly supplied disposable database

Set `COHESIVE_POSTGRES_TEST_CONNECTION_STRING` in your shell, then run:

```sh
dotnet test src/Cohesive.Assurance.Tests -c Release --filter FullyQualifiedName~PostgresReservationReplayTests
dotnet run --project eng/examples/replay-assurance -c Release -- /tmp/reservation-evidence
```

Each scenario creates its own random `assurance_...` schema, uses generated product schema and native receipt admission, and drops that exact schema in `finally`. Credentials need schema creation rights. No database is dropped. Without the connection setting, native tests are explicitly skipped and the runner exits with setup instructions.

## Bounded formal verification

```sh
./eng/examples/replay-assurance/run-formal.sh
```

This separate runner exports the same canonical claim and exact transition into
TLC inputs. It checks the atomic protocol, finds counterexamples for unprotected
retry and split state/receipt commits, witnesses lost-acknowledgement replay, and
cross-checks the generated claim predicate against the C# evaluator on all 31
sequences of up to four effects across two operation identities. It requires no
database. See [model assumptions and implementation correspondence](formal/README.md)
for bounds, provenance, prerequisites, and what the result does and does not establish.
