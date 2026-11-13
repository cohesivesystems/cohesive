# Reference batch evaluation — COH-148

Status: implemented review candidate. The [Notion semantics baseline](https://app.notion.com/p/3ec8cf7881f9811a8ff9c8ac46ea1172)
and the source-owned four-form claim definitions govern this interpreter. The older 74-case
A0 corpus described in COH-144/145 is absent from this checkout; this document does not
claim that corpus was recovered or passed. Executable cases are in
[TraceClaimEvaluatorTests](../../src/Cohesive.Assurance.Tests/TraceClaimEvaluatorTests.cs).

## Boundary

`TraceClaimEvaluator.Evaluate(claimDocument, observationBatch)` consumes already mapped
observations, not arbitrary C#, raw operation histories, or a new Simulation world/run model.
Adapters project their retained artifacts to `TraceObservationBatch`. The batch pins the
observation contract, atomic/order/clock scope, target, epoch, mapping alternatives and
complete-enumeration evidence. Every selector declares its required source partitions.
Selectors and observation contracts match exact references, including revision and fingerprint.

This is a conditional interpretation: a completeness certificate is an attributed assertion
from its source protocol. The evaluator checks its structure and scope, not its truth.
It does not infer serialization from response/arrival time, prove storage isolation, or
validate adapter conformance. Claim assumptions remain retained in the claim document.
Raw-history validation and general proof/readiness policy belong to their own interpretations.

A `SemanticObservation` contains source occurrence identity, selector, source partition/epoch,
semantic step, monotone time in 100 ns ticks, kind and input-role values. An event's payload
uses the role-neutral `EventPayload` field; `Values` must be empty for events. A missing
payload remains missing evidence. The claim determines whether an occurrence supplies the
Trigger or Witness input; adapters never relabel payloads. Non-events cannot carry EventPayload.
Successful commit inputs describe the same atomic transition under the mapping contract.
Known aborts do not trigger transition contracts; unknown commit outcomes remain inconclusive.
Initial-state selection is explicit. State membership and complete boundary coverage are
asserted by the selector's completeness protocol, not inferred from the supplied row count.

Identical redeliveries of an occurrence are deduplicated even if they arrive after later
observations. Conflicting content for the same source occurrence is invalid input. Distinct
observations must already be in semantic order; equal steps must have equal times. No sorting
silently substitutes a guessed order. One occurrence has one selector projection in this
initial wire contract; multi-selector projections need explicit distinct projection identities.

## Verdicts and closure

- State/commit predicates are conjoined over selected occurrences. A supported false
  predicate establishes a bad prefix even while the stream is open.
- Historical obligations search strictly earlier steps. An observed match suffices even
  with incomplete history; otherwise every witness source must certify complete history
  from scope origin through the preceding step before absence can violate the obligation.
- Response obligations search steps at or after the trigger with time within the inclusive
  deadline. Same-step outcomes are allowed. No match violates only when every witness
  source's completeness watermark includes the deadline. Wall-clock passage does not count.
- Matching is existential and non-consuming: one witness can satisfy multiple triggers.
- An individually satisfied obligation does not close its enclosing stream. Aggregate
  satisfaction requires every required selection source to explicitly close the finite scope.
  Ending a simulation, cancelling work or exhausting a budget supplies no such certificate.
- A closed scope ending before a response deadline is insufficient evidence of deadline
  absence. Its unresolved response remains inconclusive.
- Empty selection is vacuous only when the selected scope is complete and closed. Unknown
  populations and missing source partitions cannot become empty successful quantifiers.

Frontiers certify inclusive step/time coverage from scope origin for a selector and source.
There is at most one frontier per source/selector in a batch. Incremental frontier merging,
protocol qualification, checkpointing and late-data breach detection remain monitor work.
Corrections create a new epoch/artifact and a new result; an earlier result remains immutable.
A stateless re-evaluation does not independently detect a correction's causal relationship.

## Correlated uncertainty

Each alternative supplies one compatible joint assignment and semantic ordering. Evaluate
the entire claim in each alternative, then lift: all satisfied yields satisfied, all violated
yields violated, disagreement or an undecided alternative yields inconclusive. An incomplete
enumeration cannot establish either aggregate definitive verdict. An empty compatible domain
is an invalid-input diagnostic, never vacuous success or an inferred consistency violation.

For example, two complete alternatives assigning `ready=false` and `ready=true` establish
`ready || !ready`. Likewise, if every alternative violates a different individual obligation,
the whole claim is violated even though independently lifting each obligation would lose that
correlation. Alternatives are supplied by an independent enumeration/mapping producer; this
batch evaluator does not enumerate arbitrary missing values or call a solver.

Missing role bindings and unobserved fields yield inconclusive when needed. Explicit null
and authoritative absence (`ObservationValue.Undefined`) remain different. Missing members
inside finite collection predicates use the same evidence-sensitive rule. Incompatible or
incomplete input contracts are reported as gaps rather than converted into false predicates.
Core's existing expression evaluator owns Boolean, collection, equality and arithmetic semantics.
Unsupported operations and numeric/deadline failures remain explicit attempt outcomes.

## Example: detect overselling

The helpers below name exact model references and source projections, rather than hiding
inferred storage guarantees. The executable tests include the complete construction.

```csharp
var author = new ExpressionAuthoring();
var stock = author.Input<Inventory>("stock");
var claim = Claim.Define("stock.safe", "1", inventoryRef, observationRef, sourceRef, configRef)
    .AtEveryCommittedState(stockPopulation, InitialStateSelection.Include)
    .Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand)
    .Build();

// The adapter supplies committed states: (OnHand=10, Reserved=7), then (10, 12).
// Each row carries its occurrence, semantic step, partition and exact selector.
var result = TraceClaimEvaluator.Evaluate(claim, observedBatch);
// Violated; the second committed state's occurrence is retained as the witness.

var retainedJson = result.Serialize();
var independentlyChecked = TraceEvaluation.Reevaluate(retainedJson);
```

`TraceEvaluation` retains the claim document, exact target and observation inputs, bounds,
per-alternative obligations, matching witness identities, duplicate counts, closure/vacuity
and structured gaps. This reference-method result is not the full cross-backend COH-147
proof-state/readiness envelope. No trace success is labeled universal proof.

Persistence uses core's lossless tagged observation codec so null, undefined, temporal values
and numeric carriers remain distinct. Enum role keys use the shared strict enum catalog.
Canonical bytes are fingerprintable; re-evaluation checks exact agreement with retained results.
These are integrity checks, not signatures or independent validation of provider declarations.

## Limits and follow-on work

Limits bound alternatives, total observation/frontier records, predicate invocations and event
eligible candidate comparisons. Binary search restricts matching to strictly earlier steps
or the inclusive response window before charging visits. Arbitrary overlapping windows or
nonmatching correlations can still require quadratic work and exhaust the explicit budget. Exhaustion yields BudgetExceeded plus an
inconclusive aggregate verdict and retains original inputs. Cancellation throws and cannot
produce partial success. These limits do not promise a hard time bound inside a single core
expression evaluation; the inputs are already finite/materialized and nested collection costs
remain proportional to the actual expression work. This is the offline reference evaluator,
not the bounded-retention production monitor.

Next integrations: reconcile the absent A0 corpus; complete COH-147's cross-method evidence
policies; implement COH-149's Simulation projection/search/shrink/replay bridge; add the
incremental monitor and its batch-equivalence/protocol tests. No adapter, Simulation run,
production monitor or formal proof has been executed by this implementation.


## Authoring and integration boundary

`Claim.Define` returns `Claim.Declaration`; choosing one form returns `Claim.Predicate`;
`Require` returns the completed immutable `Claim`. Source instances use reference identity; compare derived document fingerprints for semantic
equivalence. Only the completed stage exposes `Build`,
`Attach`, `Assuming` and `RequireEvidence`. Invalid form/predicate sequencing is unavailable
in the source API, while semantic expression and contract validation still happens at lowering.

`ClaimReference.FromExecution(transition.Reference)` reuses Cohesive's typed exact execution
identity. Definition IDs are URI-escaped under `execution://`; fingerprint text comes from the owning type’s shared `ToCanonicalString()` projection.
Components are colon-separated, with embedded percent/colon escaped and ordinary profile
slashes preserved. This
projection does not independently validate the referenced artifact. Other domain selectors
and observation contracts still require explicit exact references; entity/event-specific
bridges and production consumers are follow-on integration work.

[TransitionAssuranceTests](../../src/Cohesive.Assurance.Tests/TransitionAssuranceTests.cs)
authors and interprets an inventory reservation, then checks its candidate state under an
explicit reference-model commit scope. An independent stock predicate accepts 3 reserved
units out of 10 and rejects 12. A production adapter must supply actual commit evidence;
a non-committing transition decision alone is insufficient.

The evaluator indexes binding IDs once per invocation and lazily caches portable inputs
and contract checks per observation/input within each alternative. Short-circuited inputs
remain untouched. Cache storage is bounded by observed inputs × claim inputs and released
between alternatives. This trades memory for avoiding repeated validation; it does not cache
across batches or share mutable evaluation state. Input-contract diagnostics are emitted once
per observed input, with occurrence attribution.

Evidence method `cohesive.assurance.batch/v2` records the new event payload representation,
eligible-comparison budget semantics and diagnostic deduplication. Earlier v1 evidence must
be regenerated from its source observations; it is not silently reinterpreted. Claim schema
and fingerprint bytes remain unchanged. Claim and result hashing use core's existing
`StrictDocumentJson.ComputeCanonicalSha256`; each domain retains its payload/profile authority.


Current consumers: the authored-transition reference-model test and the focused benchmark
exercise Assurance. There is no production observation adapter or ARI/ITO integration yet.
The staged source/fingerprint review also replaces the first PR revision’s URI-escaped
fingerprint triples with the shared core text format. Existing hand-authored references are
not rewritten; regenerate execution-derived claim references and their derived documents.
