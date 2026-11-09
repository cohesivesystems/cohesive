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
