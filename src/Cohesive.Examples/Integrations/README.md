# Historical market-data integration examples

These examples are compiled and discovered as tests in `Cohesive.Examples`, which is already
included in `Cohesive.sln`. They run offline without credentials, an Ito checkout or new packages.

Start with [HistoricalMarketDataExamples.cs](HistoricalMarketDataExamples.cs). Its
`HistoricalMarketData : IntegrationModel<HistoricalCoverageScope>` declares the four Requests,
recovery policy, accepted-coverage and retained-response requirements. The test binds a concrete
selection to a Tiingo operation profile, logical connection and independent consumer, compiles it, and reopens the canonical
documents. `CreateSelection` supplies the shared synthetic calendar and normalization fixtures.

Then read [TiingoCoverageEvidenceExamples.cs](TiingoCoverageEvidenceExamples.cs). It reuses that
model to demonstrate the domain interpretation and evidence boundaries:

1. Read the exact selection from the compiled property parameters.
2. Describe the acquisition request and digest-pinned Tiingo-shaped response bytes.
3. Evaluate raw OHLCV rows against the expected session set. Missing dates remain unresolved.
4. Prepare publication bytes only for complete coverage.
5. Require a confirmed sink receipt identifying the operation and exact prepared content before
   constructing a ledger advancement.
6. Interpret the advancement with the shared ledger reduction and replay its original receipt.

The test cases include complete, partial and empty responses, duplicate and unexpected dates,
invalid prices, changed source bytes and mismatched publication receipts. The evaluator is a small
fixture interpreter illustrating the contract; production Tiingo schema handling belongs in an adapter.

The evidence example exercises individual operations, rather than dispatching the generated Process.
HTTP responses and confirmed sink receipts are explicit simulated inputs. The ledger reduction is
an in-memory semantic interpretation. The example therefore demonstrates attribution and gating,
without claiming that a storage transaction committed or qualifying production durability. Ito's
integration tests exercise those physical boundaries with its real adapter and SQLite stores.

Run from the repository root:

```sh
dotnet test src/Cohesive.Examples/Cohesive.Examples.csproj \
  --filter FullyQualifiedName~Cohesive.Examples.Integrations
```

These example classes also appear in the solution's test explorer under `Cohesive.Examples`.

[ConnectionRuntimeExamples.cs](ConnectionRuntimeExamples.cs) shows the physical boundary: create a
host-owned HTTP client from a connection endpoint, register its handler against exact profile and
connection revisions, resolve the compiled binding, and execute one offline HTTP request. It does
not treat that response as coverage or publication evidence. Authentication resolution is host-owned;
Ito's adapter demonstrates the real API-key connection path.

[ScopedHistoricalDataExamples.cs](ScopedHistoricalDataExamples.cs) adds system/tenant/user defaults,
inherited allow-lists and batch limits, exact configuration snapshots, denied local overrides, and a
new-effect revocation check that retains the original connection for reconciliation.

[HistoricalQualificationExamples.cs](HistoricalQualificationExamples.cs) qualifies exact resource offers
against the original historical Contract properties. The compatible fixture is admitted with runtime
evidence pending; adjusted-only and other incompatible fixtures are rejected. The existing coverage
walkthrough now starts with this admission, so an eligible provider that returns no rows or only partial
coverage still cannot publish or advance progress. The fixtures do not attest live Tiingo access.

[LocalHistoricalDispatchExamples.cs](LocalHistoricalDispatchExamples.cs) composes scoped selection,
historical qualification, canonical document serialization and exact runtime registration. Four offline
cases count actual fixture calls after a changed default, policy denial, revoked grant or expired offer.
A new default never substitutes another connection; denied dispatch calls neither handler. Original
handler resolution remains available for reconciliation, but does not itself replay results or permit
another acquisition. These tests reuse the historical evaluator and require no Ito checkout, network,
credentials or durable-store implementation.

[HistoricalProcessExecutionExamples.cs](HistoricalProcessExecutionExamples.cs) executes the compiled
historical ingestion **Process**, using the reference interpreter and local Request handlers. The graph
owns acquisition, publication, ledger advancement, settlement and failure routing. Handlers reuse the
same coverage evaluator and native ingestion work/ledger contracts; they do not define a second sequence.

The cases cover empty, partial and complete coverage; a changed default before acquisition; policy and
freshness rejection before any effect; and restart at each successful Request boundary. Restart reopens
the canonical definition closure, serializes/deserializes the continuation and retained reply, and starts
a fresh worker. A reply retained before the interruption resumes the original graph even after current
policy denial or offer expiry, without another provider call, publication or ledger advancement.

The sink and retained work are in-memory fixtures. These tests qualify reference execution and recovery
from a **known retained result**, not disk persistence, process termination or recovery from an unknown
external effect. They do not retry ambiguous effects or claim production storage durability.

[HistoricalLifecycleExamples.cs](HistoricalLifecycleExamples.cs) adds the COH-136 local admission-control
loop around that same Process. Revision-aware pause rejects a new session while the
admitted session finishes from its retained result through a fresh worker. Successful exact Process
completion ends draining. Failed work stays visible until an explicit verified resolution is accepted;
these tests do not infer resolved external effects from a failure status.

[HistoricalHealthExamples.cs](HistoricalHealthExamples.cs) uses the internal experimental `IntegrationInspectionHost` for evidence retrieval, clock sampling and JSON export. This is test/example infrastructure, not a production hosting contract.
A synthetic source can be operationally Healthy while returning zero or only one of two required sessions.
The view separates source coverage, digest-checked accepted publication, source ledger progress and
consumer-wide versus exact-binding pending sessions. Expiry changes provider health/readiness to Unknown without deleting accepted evidence.
The example exercises the real compiled Process with the existing local handlers and performs no live I/O.


[HistoricalRecoveryExamples.cs](HistoricalRecoveryExamples.cs) exercises the next COH-136 recovery boundary:
a publication reaches the local sink but its Request outcome is lost before reply retention. The local
host keeps the exact claimed Request fingerprint, blocks repeat dispatch and leaves lifecycle draining
while paused. An authorized operator can reconstruct only the publication reply whose retained sink
receipt, Request fingerprint and destination content digest agree. The canonical Process then decides
ledger advancement and settlement; reconciliation neither activates admission nor restarts acquisition.

The negative cases cover a lost worker before the effect, corrupted destination bytes, a changed reply
target under the same dispatch identity, unauthorized resolution (including replay), and acquisition
with no retained reply. None guesses an outcome or performs another provider call. Missing evidence
is not evidence of absence: it cannot authorize a retry. A healthy provider would not change this rule.

This is a single-threaded, in-memory executable policy experiment, not a public recovery API or durable
journal. The simulated sink retains its receipt and exact Request association together before the injected
post-handler interruption. A production adapter must establish that atomic evidence boundary, authenticate
the operator, fence concurrent workers and retrieve trustworthy evidence. Existing Process controls and
distribution reconciliation remain the execution authorities; this example adds neither a workflow engine
nor a durable-store abstraction. The separate failure-resolution example below exercises terminal failure resolution and operator revision
fencing. Bounded backfill and the production control projection remain follow-up work. Ito is unchanged.


[HistoricalFailureResolutionExamples.cs](HistoricalFailureResolutionExamples.cs) uses the library's
`ResolveFailure` operation to close a fully accounted coverage failure while admission is paused.
A trusted host verifier checks the evidence locator and supplies the exact failed Process decision;
the fixture establishes that the source read answered and no publication or ledger effect began.
The controller retains the original command, decision and resulting control revision. Successful
completion remains absent and the ledger remains unchanged. Failure status alone never authorizes
resolution. Tests cover malformed/stale/unauthorized commands, missing and mismatched evidence,
verifier exceptions, serialized command replay, concurrent exact retries and revision changes during
verification. Production evidence authentication and durable storage remain outside this local fixture.
