# Replay assurance demo implementation plan

Status: implementation begun in isolated `codex/replay-assurance` worktree. Aspire/CQRS composition remains untouched.

Delivered foundation: narrow [A0 semantics](../../src/Cohesive.Assurance/docs/a0-effect-uniqueness.md), independent fixture table, core-only Assurance claim/observation/evaluator/document profile, expect and generated checks, and an in-memory commit-protocol acknowledgement-loss integration. This is not full A0 completion. The [standalone reservation runner](../../eng/examples/replay-assurance/README.md) now adds native PostgreSQL lost-acknowledgement coverage and an unprotected-retry counterexample with an independent stock-update audit. Its script owns and removes a disposable Docker database. General Simulation bridge and formal checking remain follow-ons.

Design authority: [Cohesive.Assurance Vision](https://app.notion.com/p/cohesivesystems/Cohesive-Assurance-Vision-3ec8cf7881f9811a8ff9c8ac46ea1172), draft retrieved October 26, 2026. Sections 4, 6, 9, and 11 govern semantics, evidence, ownership, and milestones. This demo is a narrow contribution to A0–A2 with a bounded S2-style fault fixture; it does not complete those milestones or replace the vision's production-monitoring delivery scope.

## Outcome and scope

Demonstrate that retrying one exact Process-invoked inventory reservation occurrence after losing its acknowledgement does not commit another stock decrement. Produce an inspectable property, a reviewed explanation, generated-case evidence, and a real PostgreSQL regression. A deliberately defective test realization must produce a counterexample.

The first slice is operation replay assurance, not whole-workflow crash recovery, business-intent deduplication, external payment uniqueness, or asynchronous read freshness. Formal verification follows as a separate interpretation; it is not a prerequisite for delivering this slice.

## Existing semantic authorities

| Concern | Existing authority and implementation | Planned use |
| --- | --- | --- |
| Reservation decision | [Aspire fulfillment domain](../../eng/examples/aspire-first/Orders.Model/FulfillmentDomain.cs), `InventoryTransitions.Reserve` | Use its guarded decrement as the demo behavior; do not alter its declaration in parallel. |
| Exact operation identity | [ProcessOperationOccurrence](../../src/Cohesive.Storage/Processes/ProcessDurableCheckpoint.cs) and [EntityTransitionOperationRequest](../../src/Cohesive.Storage/EntityTransitionOperationRepository.cs) | Preserve occurrence, authority, subject, exact transition reference, and input validation. |
| Commit and replay semantics | [EntityTransitionCommitProtocol](../../src/Cohesive.Storage/EntityTransitionCommitProtocol.cs), canonical commit, receipt, and disposition types | Execute and observe the existing protocol rather than implementing retry semantics in the demo. |
| Provided capability | `EntityTransitionOperationCapabilities.SupportsAtomicStateAndReceipt` and its partition constraint | Project support evidence from this existing declaration; do not add another capability enum. |
| Process attachment | [ProcessTransitionOperationBinding](../../src/Cohesive.Storage/Processes/EntityTransitionProcessOperationAdapter.cs) | Reuse exact plan, repository, partition, and receipt-contract checks. |
| Shared adapter assertions | [EntityTransitionReceiptConformance](../../src/Cohesive.Tests/Storage/Conformance/EntityTransitionReceiptConformance.cs) | Extend common checks; existing coverage includes concurrent replay, stale fences, and creation replay. |
| Native PostgreSQL fixture | [PostgresRepositoryConformanceTests](../../src/Cohesive.Tests/Storage/Conformance/PostgresRepositoryConformanceTests.cs) | Reuse disposable schema ownership, receipt binding, and opt-in database conventions. |
| Generated cases | [PropertyCases](../../src/Cohesive.Simulation/Generation/PropertyCases.cs) and typed `CheckProperty` | Reuse generation, classifications, shrinking, and exact replay coordinates for synchronous model exploration. |
| Retained scenarios | [Scenario documents and traces](../../src/Cohesive.Simulation/docs/scenarios.md) | Follow existing canonical serialization and provenance patterns; do not pretend current traces record commit faults. |

### Identity correction to the exploratory sketch

Ordinary updates replay by exact Process operation occurrence. `EntityTransitionOperationRequest.IntentFingerprint` omits occurrence but is specifically reserved for creation replay after creation authority has been established. It must not become an update deduplication key.

Repeated transport or repository calls in the demo retain the same canonical occurrence and exact request. A new Process attempt or occurrence is not automatically the same update operation. Changed input under a retained occurrence must exercise the existing identity-conflict behavior. Document the actual native lookup key and validation sequence before implementing the observer; do not group only by complete request fingerprint, which would separate conflicting requests and hide identity misuse.

## Design decisions

1. Add one portable, specialized property: at most one committed effect for an exact entity-transition operation occurrence. Do not start with `ContractModel`, a general temporal expression language, or a framework-wide assurance registry.
2. Establish the minimal shared semantic slice in `Cohesive.Assurance`, depending only on core `Cohesive`, as required by the vision. Claims, observation contracts, reference verdict semantics, and evidence belong there from the outset. Storage retains operation identity and commit semantics. Local observation bindings project those into Assurance using existing portable references; Assurance must not reference Storage or Simulation. Keep fault wrappers and scheduling fixtures local. Add the optional `Cohesive.Simulation.Assurance` bridge only for the actual trace/property integration needed here.
3. Represent the property as an immutable versioned Assurance claim referencing the exact transition definition. Reuse core Expr, existing identity, fingerprint, diagnostic, and strict-document primitives. Express uniqueness over complete finite observations using existing collection/Relations meaning; A0 must specify its precise lowering before evaluator work. Avoid a storage-specific assertion language. Tests consume the same claim rather than restate it as independent lambdas.
4. Keep execution mechanics in shared test fixtures initially. A compact native probe artifact may retain durable commit observations missing from current scenario traces, but it is source evidence mapped through an Assurance observation contract, not a second semantic trace model. Reuse existing request, receipt, and disposition types. Extend the existing Simulation trace contract minimally for correlation/closure if needed; do not hide missing evidence behind a parallel trace format.
5. Evaluate actual committed-effect evidence, not merely returned `Committed` dispositions. The acknowledgement can be missing after the effect occurs. For the isolated stock fixture, independent before/after state and retained receipt evidence establish the effect; do not infer general event counts from final state when compensation or unrelated writers are possible.
6. Use the vision's shared evidence envelope and separate claim acceptance, attempt lifecycle, backend support, verdict, proof state, disposition, and readiness. Verdicts are satisfied, violated, or inconclusive. An open non-violating safety prefix is inconclusive with a separate no-violation-observed fact. Satisfaction requires an explicit closed scope; test completion, cancellation, and budget exhaustion are not closure. Generated runs without violations leave the general claim proof state unknown. A missing native test is not a pass.
7. Preserve performance boundaries: no new default production tracing, serialization, or allocations on the commit hot path. Use a test-side wrapper that observes the real repository result before withholding it from the caller.

The priority is a small inspectable semantic surface and decisive evidence. Generality, automatic contract attachment to all endpoints, and a public fault scheduler are deferred until demonstrated by another use.

## First slice implementation sequence

### 1 Establish the exact claim and observation contract

Begin with a narrow A0 document and independently reviewed conformance fixtures before implementing evaluation. Specify committed-effect identity versus duplicate telemetry, exact occurrence correlation, ordering scope, missing observations, sampling, completeness, and scope closure. Include witnessed duplication, duplicate observation of one effect, incomplete history, open non-violating prefixes, closed scopes, vacuity, and inconsistent assumptions. No global order may be inferred from timestamps.

The initial claim is single-trace effect uniqueness. It is not the broader relational idempotency claim comparing executions with and without retries; that remains a distinct family in the vision.

- Add the narrow Assurance claim and strict persisted representation using established serialization and fingerprint facilities. Keep unsupported assertion families explicit; no generic ContractModel facade is required.
- Reference the exact transition definition; include explicit scope and observation requirements. The property is a safety claim over declared executions, not a claim that every request succeeds. A finite test execution is complete only when its observation contract establishes closure of its declared scope.
- Implement one evaluator and a deterministic explanation projection. Keep explanatory prose derived from the declaration and evidence.
- Record separate facts for attempted invocation, observed durable completion, acknowledgement disposition, retry result, and final authoritative state. Retain ordering and coverage; do not infer a total concurrency order from wall-clock timestamps.
- Include a positive reachability/control case proving that a valid reservation can commit. An implementation that rejects every request must not satisfy demo acceptance solely because no duplicate effect occurs.

Acceptance: deterministic round trip and fingerprint checks, foreign-definition rejection, identity-conflict scope tests, incomplete-evidence handling, and a known duplicate-effect trace that fails the same evaluator.

### 2 Exercise the existing protocol with a lost acknowledgement

- Build a stock fixture with ten available units and a reservation of two. Prefer referencing the existing Aspire model from the demo runner where project boundaries permit; reusable conformance helpers accept a prepared commit and repository rather than depending on the example project. An independent stock test fixture is a test input, not a second application authority.
- Commit through the actual repository protocol, retain private observer evidence, then throw a test transport exception instead of returning the result to the caller.
- Retry the exact request/commit through the established replay path. Verify final stock is eight, one effect occurred, and the retained result agrees with the first commit.
- Repeat using a fresh repository/session to avoid relying on caller-local memory. This tests persistence across client reconstruction, not database crash recovery.
- Add changed-input, stale-fence, concurrent-identical-call, and distinct-occurrence controls by extending the common conformance cases where applicable.
- Use a deliberately defective test-only realization that applies the decrement twice. Its failure must be detected from state/effect evidence even if it reports replay on the second call.

Acceptance: the same property evaluator accepts the valid protocol and rejects the defective realization. No changes to production retry behavior or injected production failure hooks are necessary.

### 3 Add expect and generated-case evidence

- Use existing xUnit string equality and checked-in text expectations; no new snapshot-testing dependency is required. Compare stable property explanations and the minimal counterexample. Preserve exact evidence separately from normalized display text.
- Reuse Simulation generators for stock, quantity, retry count, and a small finite schedule description. Initially explore sequential retries, acknowledgement loss, rejection, and identity mismatch; concurrency remains explicitly orchestrated conformance coverage rather than a claim of exhaustive generated scheduling.
- Run synchronous generated checks against a bounded protocol test interpreter using the same property evaluator. Preserve generator definition, seed, classifications, and shrink/replay coordinates.
- Current `CheckProperty` evaluation is synchronous. Do not block on PostgreSQL tasks inside it. Replay a bounded generated corpus asynchronously against the native fixture and retain per-case results. Async shrinking of live protocol runs is deferred unless a minimal shared asynchronous runner extension is justified during implementation.
- Require coverage of successful commit plus lost acknowledgement plus retry; report rejected/discarded cases separately. Shrinking must retain the causal prerequisites for the failure.

Acceptance: a deterministic generated run, replay of its counterexample, stable expected explanations, and native execution of retained cases. Generated model evidence and native corpus evidence remain separately labeled.

### 4 Produce the runnable demo and retained report

- Add a standalone runner under `eng/examples/replay-assurance/`, referencing existing libraries and the shared fixture as appropriate. Keep Aspire composition unchanged.
- Offer an offline run and an explicit PostgreSQL run using a disposable database. Reuse the `COHESIVE_POSTGRES_TEST_CONNECTION_STRING` convention where sharing the conformance fixture; isolate all created objects and clean up only owned schema resources.
- Emit canonical property and evidence artifacts plus a short deterministic explanation. Record source revision/dirty state, exact transition/property fingerprints, interpreter or adapter build identity, case coordinates, coverage, and limitations. Do not expose connection strings or private payloads in the presentation artifact.
- Present the declared property, successful commit with lost acknowledgement, replay with unchanged stock, and the failing counterexample from the defective test realization.

Acceptance: one documented offline command and one native command reproduce the narrative. Native success is claimed only after the native run passes. This is a command-line demo artifact; a dashboard or video recording is subsequent presentation work.

## Change boundaries for parallel work

This initiative owns the minimal Cohesive.Assurance A0/A1/A2 contribution, dedicated fixture/demo files, expected outputs, and focused conformance additions. Coordinate package ownership if another implementation of the Assurance vision is underway; never introduce a competing claim or evidence schema. Prefer new partial test files over concurrent edits to large existing fixtures. Any necessary shared fixture extraction should be small and separately reviewable.

The Aspire/CQRS initiative owns `eng/examples/aspire-first` composition, infrastructure attachment, materialization and projection APIs, history placement, read-progress semantics, and commit-token contracts. This slice consumes current interfaces and does not redefine them.

Use an isolated worktree for implementation. Before shared core or adapter changes, inspect the other initiative's latest interface changes and agree on ownership. This plan does not authorize sending messages to another chat. A shared-file collision is a coordination checkpoint; it is not a reason to fork semantic types.

Integration requires only the exact reservation transition reference, prepared repository/binding, the scoped property declaration, and retained evidence. No changes to API response shapes or production commit tokens are required for the first slice.

## Bounded formal verification slice (delivered)

Add an explicit bounded protocol model only after the observation vocabulary and claim evaluator are stable. Consume the same Assurance claim/evidence contracts; introduce no verifier-specific proof-state taxonomy. Model the occurrence fence, atomic state-and-receipt action, acknowledgement loss, replay, and competing attempts. State environmental assumptions and bounds independently of the property.

The first approved TLC integration remains example-local under `eng/examples/replay-assurance/formal`, with `run-formal.sh` as its standalone entry point. Promote it to a reusable adapter only when multiple claim/model integrations establish a stable shared boundary. Translate the same specialized property declaration; do not hand-maintain a second property formula. Retain generated model source, translation version, model fingerprint, assumptions, limits, exploration completion, and counterexample mapping. Test lowering against known good and bad bounded traces.

Check that a successful effect is reachable and that a defective protocol is rejected. A completed finite-model result establishes the property in that model; a timeout or unsupported construct does not. An initially handwritten protocol model is an attributable abstraction of the implementation, not proof of compiler correctness. Native conformance and trace replay provide supporting evidence while general refinement remains unproved.

## CQRS integration follow-on

Once the other initiative stabilizes its materialization progress and write-evidence contracts, introduce a separate read obligation: a token-bearing read includes the acknowledged position or returns the declared pending outcome after its wait budget. Reuse those exact progress identities and capabilities. Do not add parallel token, cursor, or watermark types here.

This is the second property family that should drive an abstraction audit of the minimal Assurance implementation. Generalize the existing shared claim, observation, and evidence mechanisms only where their semantics genuinely coincide; extract reusable local scheduling or binding mechanics when justified. Bounded read response and effect uniqueness remain distinct properties.

## Delivery and validation

Deliver in three reviewable changes: (1) property plus evaluator and observation fixtures; (2) native replay scenario plus shared conformance and generated cases; (3) runner, artifacts, expected explanation, and documentation. Each change should build and retain a useful verification boundary.

Run focused Storage conformance tests in `src/Cohesive.Tests`, relevant Simulation tests when generation integration changes, and native PostgreSQL conformance with the configured disposable database. Do not report gated tests as executed. Finish with an authority/type audit, checks that generated and example paths consume the same property, and inspection that explain output does not overstate evidence.

The first slice is complete when a reviewer can inspect the property, reproduce a lost-acknowledgement retry against PostgreSQL, see a rejected defective realization, replay generated cases, and distinguish observed evidence from future formal claims—without depending on unfinished Aspire/CQRS changes.

The delivered finite model uses two clients, two operation identities and two attempts
per client. Atomic receipts pass; unprotected retry and split state/receipt commits
produce counterexamples. A separate witness reaches lost acknowledgement followed
by replay. Thirty-one closed effect sequences cross-check the generated uniqueness
predicate against the C# evaluator. Exact claim/transition, assumptions, model/config
inputs, tool and source hashes, raw logs and scoped results are retained. Counterexample
translation to native regression traces and a mechanical refinement proof remain deferred.
