# A0 effect uniqueness semantics

Status: executable first profile, not completion of the Assurance vision's full A0 language.

This profile interprets one single-trace safety claim: for each exact operation occurrence in a selected authority and subject, at most one distinct durable effect of the exact referenced transition occurs. It does not establish relational idempotency, original-result replay, business-intent deduplication, or progress. The claim has stable identity/revision; its content includes the exact transition reference. No C# callback survives authoring.

## Observation contract

The observation contract pins its identity, revision, mapping fingerprint, authority scope, and subject identity. The mapping fingerprint identifies the adapter's observation rules, not merely its name. A batch also pins the observed transition, target kind (reference model, implementation candidate, deployed system), exact target revision/fingerprint, and scope completion. Target identity is evidence, not a verified attestation of deployed software.

An observation supplies a globally distinct durable effect identity within this selected scope and an exact operation occurrence identity. Reobserving the same effect for the same operation is duplicate telemetry and counts once. Two distinct effect identities for one operation are a violation. Reusing one effect identity for different operations contradicts the observation contract: diagnose the whole batch rather than reporting a proof from impossible evidence. Effect IDs identify physical committed effects, not operation IDs or receipt IDs that might hide a second commit.

Operation identity must preserve the owning execution protocol's authority and occurrence distinctions. The update adapter must not substitute a complete request fingerprint (which changes with input), business key, or creation-only intent fingerprint. The selected transition reference must exactly match the claim. A mismatch is invalid evidence, never silent filtering.

Null operation or effect identity means missing evidence. Empty/whitespace identities are malformed. Nullable domain values are not modeled here. The supplied order is retained for witness locations only; uniqueness is order-insensitive. No global order, clock, or linearization is inferred. Time, sampling, and partial-order reasoning are unsupported. Sampled/incomplete acquisition is represented as incomplete coverage and cannot yield satisfaction. These limits are versioned by the profile and mapping contract.

## Verdict and closure

For each operation, form the set of observed distinct effect identities. The mathematical claim is that every such set has cardinality at most one. This reuses ordinary finite set uniqueness semantics; this profile has no separate scalar predicate language or expression evaluator. Future scalar predicates must use core Expr.

- A consistent batch witnessing two distinct effects for one operation is violated, even when coverage is incomplete or the scope is open.
- Otherwise an open scope is inconclusive: a later admissible effect could violate the claim.
- Otherwise a closed incomplete scope, or a batch with missing identity evidence, is inconclusive.
- Otherwise a closed complete scope is satisfied for that scope only. Empty scope is vacuously satisfied, with zero observed operations/effects recorded separately.
- Contradictory evidence or a foreign transition yields an inconclusive verdict with an error diagnostic. No conclusion is drawn from an empty set of admissible completions.

ClosedComplete is an explicit producer assertion that the declared scope has ended and every relevant effect was acquired. End of test, cancellation, timeout, or end of a search budget is not this assertion. A selected operation scope can close only when the producer establishes that no further attempts in that scope can commit and acquisition is complete. This evaluator checks the supplied contract, not the truth of that external assertion. Evidence must retain it for review.

Missing evidence does not erase a concrete, consistent counterexample. Contradictory observations do: they invalidate the observation mapping and need resolution. Evaluation scans the entire batch before publishing a verdict, so an early duplicate effect cannot hide a later contradiction.

## Evidence and limitations

Evidence retains the exact check input, its canonical fingerprint, evaluator/profile version, verdict, first pair witnessing duplication, coverage counts, and structured diagnostics. The first pair is selected by supplied observation order and indexed into that immutable input. No-violation-observed is a fact separate from satisfaction. No global proof state or readiness is inferred. Generated successful checks do not prove the unbounded claim.

Fingerprinting is deterministic identity/integrity, not authentication or evidence reuse authorization. Observation mapping and target changes change the fingerprint. Reuse still needs dependency reassessment. Serialization rejects unknown members, duplicate JSON properties, malformed identities, unsupported profile versions, and mismatched persisted fingerprints.

## Conformance table

The independently authored JSON cases in the test project specify these expected results before the evaluator implementation:

| Case | Result |
| --- | --- |
| Empty open scope | Inconclusive, zero coverage |
| Empty complete closed scope | Satisfied, vacuous |
| One effect in an open scope | Inconclusive |
| One effect in a complete closed scope | Satisfied |
| Same effect observed twice | Count once |
| Distinct effects for distinct operations | Satisfied when closed complete |
| Distinct effects for the same operation | Violated, including open/incomplete scopes |
| Missing effect or operation identity | Inconclusive unless an independent consistent violation exists |
| Same effect attributed to different operations | Invalid evidence, inconclusive |
| Violation followed by contradictory evidence | Invalid evidence, inconclusive |
| Foreign transition revision/fingerprint | Invalid evidence, inconclusive |
| Closed incomplete empty scope | Inconclusive, not an empty complete population |

Only this profile is admitted; general Boolean/temporal composition is not approximated by three-valued truth tables. The complete Assurance A0 quantifier, history, bounded-response, and scalar/operator catalog remains future work.
