# Bounded reservation protocol verification

Run `./eng/examples/replay-assurance/run-formal.sh` from the repository root.
Requires .NET 10, Python 3, curl, and Docker. No database or Aspire host runs.
The runner downloads official TLC 1.7.4, verifies its pinned SHA-256, and runs
Java in disposable `eclipse-temurin:21-jre` containers. The Java image is a
version tag, not a content pin; the TLC jar and all exported inputs are hashed.
An optional argument selects a **new** artifact directory.

## Claim authority and interpretation

`ReservationClaim.Create` is shared by the PostgreSQL demo and formal export.
Both bind `EffectUniquenessClaim` to the exact compiled `Reserve` definition.
The export retains canonical claim JSON, its hash, and transition reference.
The local interpreter lowers this one supported claim type to `AtMostOneEffect`:
for every exact operation occurrence, at most one distinct durable effect exists.
Each `effects` sequence position denotes a distinct physical commit; the value
is its operation identity. Sequence order is bookkeeping, not a claimed global
runtime clock. Replayed responses append no effect. Duplicate telemetry is not
modeled; the native evaluator separately deduplicates effect identities.

The protocol model is handwritten and its correspondence is manual. The export
does **not** compile transition guards, SQL, locks, or transactions from C# into
TLA+. Fixed quantity and initial stock reuse demo constants; the reservation
arithmetic is an explicitly maintained abstraction. Only effect uniqueness is
the shared claim; `TypeOK` and `StockAccounting` are auxiliary model checks.

## Finite scope and assumptions

- Two clients, two possible operation occurrences, at most two attempts per
  client, one stock row initially ten, fixed reservation quantity two.
- Initial assignment explores every client-to-operation mapping, including two
  clients sharing an occurrence. Each client keeps its occurrence across retry.
  All requests for an occurrence have identical transition, subject and input.
- AtomicReceipt has one durable linearization point for state and receipt.
  The receipt is unique per occurrence and retained for the whole execution.
  Receipt corruption, identity/hash collisions, eviction, migration, changed
  requests, unrelated writers, multi-authority transactions, and external
  payment side effects are outside this model.
- A pre-commit rollback leaves no durable state. An acknowledgement can be lost
  after any successful commit or replay. Client loss preserves database state;
  retry retains identity. No durability failures after COMMIT are modeled.
- Commit and replay abstract completed repository operations. Database internal
  steps are collapsed, not verified. Competing different operations serialize;
  optimistic conflicts may be represented by Rollback, not silent extra effects.
- No fairness assumption or liveness/property of eventual success. The attempt
  bound is explicit in Start, not a TLC depth cutoff or state constraint that
  could silently prune successors. Exhaustion permits stuttering; deadlock
  checking is disabled intentionally. It is not production scope closure.

TLC exhaustively explores reachable states within these bounds. A completed
positive run is bounded model satisfaction, not an unbounded inductive proof,
not evidence that every PostgreSQL execution refines this model, and not a
production assurance gate. Timeout, parse errors and unexpected failures cannot
produce satisfaction. TLC uses fingerprints for state storage; retain its log
including collision estimates and explored-state statistics.

## Implementation correspondence

| Model | Existing implementation / evidence | Status |
| --- | --- | --- |
| Stable `operation[c]` | `ProcessOperationOccurrence` serialized and hashed by `OperationKey` in `PostgresTransitionReceipts.cs`; same exact commit in `ReservationReplayDemo` | Native retry test exercises this; collision freedom assumed |
| Atomic `Commit` | `TryCommitTransitionOperation`: native transaction, operation/subject advisory locks, `UpsertCore`, unique receipt insertion, then `CommitAsync` | Atomic abstraction; SQL and lock implementation not formally verified |
| No second committed effect | Unique receipt fence; losing insert or concurrency conflict rolls back the transaction, including the write, before shared resolution | Atomic model hides tentative writes/rollback; requires all writes use this protocol |
| `Replay` | `EntityTransitionCommitProtocol` resolves retained receipt and validates commit fingerprint; native demo checks Replayed | Exact replay branch, not creation intent deduplication |
| `Rollback` | Transaction disposal/concurrency failure before commit | Nondeterministic abstraction; not all error classification modeled |
| `LoseAcknowledgement` | Injected caller fault after native COMMIT, dispose original pool, rebuild repository, retry | Native test covers one schedule; model explores others |
| `effects` append | Transaction-local audit trigger; distinct committed audit row identities | Model has perfect observations; fixture acquires all rows |
| `UnprotectedRetry` | Demo re-reads/re-evaluates Reserve and Upserts without receipts | Deliberately defective realization |
| `SplitReceipt`, `CrashBeforeReceipt` | Hypothetical state commit followed by separate receipt commit | Mutation control only; **not** current PostgreSQL adapter behavior |

## Results and retained artifacts

- AtomicReceipt must finish with no invariant violation.
- UnprotectedRetry must violate `Claim` with a retained counterexample.
- SplitReceipt must violate the same `Claim`, demonstrating why atomicity matters.
- LostAckReplayWitness intentionally checks the false `NoLostAckReplay`
  invariant under AtomicReceipt. Its counterexample witnesses a reachable lost
  acknowledgement followed by replay; it is a non-vacuity check, not a defect.

`bounded-verification.json` retains exact claim, model bounds, assumptions,
input/tool/log hashes, checker exits and scoped results. Relevant implementation
and exporter sources are snapshotted and hashed for manual correspondence review;
these snapshots do not establish a refinement proof. Raw TLC logs retain
counterexample states and exploration statistics. Exported TLA/configs can be
rerun independently. A violation is an abstract model counterexample; this slice
does not translate it into a native executable regression trace. Evidence is a
local report, not a signed attestation. Generic proof-job/result APIs and a
formal refinement mapping remain deferred rather than being invented here.

Tool reference: [official TLA+ tools usage](https://github.com/tlaplus/tlaplus/blob/master/USE.md).

The runner also checks `ClaimConformance`: all 31 effect sequences of length zero
through four over two operation identities. Expected truth values come from the
existing C# reference evaluator on closed, complete, consistent inputs; TLC
checks the generated predicate against those values. This checks interpretation
agreement within that finite domain. Missing identities, contradictory telemetry
and open prefixes remain evaluator conformance concerns, outside the model's
perfect-observation assumption.
