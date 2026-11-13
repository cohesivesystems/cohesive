# Evidence and proof state

`ClaimEvidence` retains one immutable interpretation report for an exact derived claim and
`EvidenceSubject`: target kind, target revision, effective realization configuration, full
claim scope, and model/encoding/realization obligation. It carries the existing `ClaimDocument`,
so authoritative source, compiler/profile/configuration, derived fingerprints and reconstructible
source maps remain owned by the claim pipeline. Runtime evidence is a report, not editable claim IR.

`ClaimAssessment` derives proof state from these reports under explicit acceptance rules. It
retains the policy reference, effective rules, original reports, unavailable dependencies,
disposition and explicit assessment time. Restoring an assessment recomputes its derived fields.
A changed policy, dependency or clock produces a new assessment; prior evidence is unchanged.

## Trace evidence

```csharp
var subject = new EvidenceSubject(
    EvidenceTargetKind.Implementation,
    Target: binding.Target,
    Configuration: candidateConfiguration,
    Scope: allOrderInputs,
    Obligation: ProofObligation.Realization);

var evidence = ClaimEvidence.FromTrace(
    run.Evaluation, subject, mappingConfiguration,
    dependenciesComplete: true,
    dependencies: [acceptedAtomicityContract],
    sharedDependencies: [sharedPolicyPredicate]);

var restored = ClaimEvidence.Deserialize(evidence.Serialize());
var assessment = ClaimAssessment.Evaluate(
    restored, acceptancePolicy,
    acceptance: [EvidenceAcceptance.Of(restored)],
    asOf: reviewTime);
```

The caller must supply resolved exact references and explicitly declare whether the dependency
slice is complete. `true` is an assertion about dependency discovery, not proof of provider truth.
Trace evidence is restricted to realization obligations on implementation or deployment targets;
it cannot be relabeled as a model or encoding proof. Scope and configuration remain explicit
adapter attribution because the existing trace contract does not independently attest those facts.
Trace capture accepts the evaluator-owned `TraceEvaluation` sealed class directly. Its constructors
are internal and properties are read-only; callers cannot create unchecked results or use record
`with` copies. A JSON converter reads a private wire DTO, re-evaluates the inputs, and compares the
complete result before returning the public type. No trust registry or global validation cache exists.
Core observation factories snapshot ordinary CLR objects and caller-owned dictionaries before
execution; the result retains those immutable portable snapshots, not the original objects.
Evidence also records mapping, epoch, alternative, closure and enumeration references as dependencies. Failed
or bounded validation cannot claim a fully inspected dependency slice. Original Simulation
execution attribution and scheduled inputs can still be retained using `ScenarioAssuranceArtifact`;
this core package neither depends on Simulation nor claims that re-evaluation executed a candidate.

A valid violated trace can yield `Disproven` for its pinned target under an accepted mapping.
A clean finite trace yields `Unknown`, even when the selected trace population is closed and
its trace verdict is `Satisfied`. The faulty/repaired Simulation fixture exercises both outcomes.

## External proof, exhaustive checking and declarations

```csharp
var report = new ClaimEvidence(
    claim, modelSubject,
    kind: EvidenceKind.FormalProof,
    method: exactVerifierVersion,
    configuration: exactVerifierOptions,
    support: InterpretationSupport.Supported,
    status: AssuranceAttemptStatus.Completed,
    verdict: ClaimVerdict.Satisfied,
    coverage: EvidenceCoverage.RestrictedScope,
    dependenciesComplete: true,
    dependencies: [encodingRevision],
    residuals: [finiteInventoryBound],
    sharedDependencies: [predicateLowering],
    artifacts: [verifierReport, encodedModel]);
```

An accepted complete proof or exhaustive check yields `Proven` over full declared scope, or
`Conditional` when narrower bounds/assumptions are named as residuals. Restricted coverage must
name residuals; full coverage cannot hide them. A formal refutation can lack an executable
counterexample: its verifier report is retained without manufacturing a Simulation witness.

The same report shape represents `ProviderDeclaration`. Policy acceptance matches **kind,
exact method and exact configuration**; accepting a proof checker does not accept a declaration.
Accepted declarations remain labeled declarations even when policy allows them to establish a
claim. Existing capability assertions are referenced as artifacts rather than copied into a
new capability algebra. Caller-supplied policy references/rules are attribution, not authenticated
authorization; callers must validate their authority outside this library.

No external verifier is invoked here. External reports, coverage, support and domain membership
are assertions by adapters accepted under policy. Refutations must apply to admissible behavior
inside the declared claim scope; a spurious encoding counterexample cannot be reported as a
realization refutation. Serialization verifies consistency and exact
identity, not truth or authenticity. TLC/Alloy/Lean adapters and encoding conformance qualification
remain follow-on work. A model proof never automatically discharges an encoding or realization
obligation, even when their target identifiers happen to coincide.

## Independent dimensions

| Dimension | Meaning |
| --- | --- |
| `AssuranceAttemptStatus` | Completed, invalid observations, unsupported, budget exceeded, timeout, cancellation, unknown or incomplete exploration |
| `ClaimVerdict` | Satisfied, violated or inconclusive within the method's scope |
| `InterpretationSupport` | Interpreter support declaration for this exact claim |
| `ProofState` | Unknown, proven, conditional or disproven under acceptance policy |
| `ClaimDisposition` | None, waived or requirement revised, with authority and reason |

Noncompleted or unsupported reports cannot carry definitive verdicts. Unsupported, timed-out,
cancelled, unknown and incomplete attempts stay distinct in retained evidence. Sampled success,
unknown dependencies and unaccepted methods cannot produce proof. A mismatched claim or subject
throws at assessment construction, even when other reports match; callers must partition reports
explicitly. The single-report overload derives the boundary from the report, and `EvidenceAcceptance.Of`
removes tuple copying while leaving acceptance an explicit policy decision.

A full-scope accepted proof conflicts with an accepted refutation of the same subject. A conditional
proof does not set `ConflictingEvidence`: the current envelope has no certificate that a counterexample
lies inside that restricted domain. This conservative rule can leave a real bounded conflict unflagged;
explicit domain-membership evidence is needed to strengthen it. Refutation still takes precedence. Repeated reports are deduplicated, never counted as independent votes.
Shared predicate and lowering dependencies remain explicit.

Waivers require authority, reason and expiry. At the expiry instant they are ineffective. Requirement
revisions require an accepted semantic-diff/revision reference. Neither changes proof state: a
waived refutation remains `Disproven`. `DispositionEffective` is not a readiness decision.

## Reassessment and retention

Supply exact **old references** in `unavailableDependencies` when a dependency changes, is refuted,
or can no longer be relied on. Dependencies include all claim references, target/configuration/scope,
method/configuration, residuals, shared dependencies and supporting artifacts. Unknown dependency
slices block acceptance. This API consumes an explicit change set; it does not watch a workspace,
resolve external documents, infer transitive dependency equivalence or automatically reassess
other claims. A refuted assumption invalidates dependent support; it does not itself refute every
claim that relied on that assumption.

Both evidence and assessment JSON reject unknown fields, duplicate fields, incompatible schemas,
and inconsistent derived data using the existing strict canonical serializer. Evidence identities
use the shared core fingerprint helper. Trace data is a nested structured document, not escaped JSON
inside a JSON string. External reports are not re-proved during restore; trace reports are re-evaluated.
Once verified, in-memory capture and assessment do not repeat that evaluation. Hashes are integrity identities, not signatures or reuse authorization.

These are offline retained-attempt operations. Whole documents are materialized, and restoration
can re-evaluate embedded traces. They are not a streaming production monitor or an untrusted-input
resource isolation boundary. Campaign/replay details, bounds, reductions, witnesses and verifier
output are domain-owned exact artifact references; adapters must retain and resolve those artifacts.

The general readiness engine, automatic dependency-change propagation, tool adapters, and production
monitoring remain separate work. The API rename `TraceEvaluationStatus` → `AssuranceAttemptStatus`
is source-breaking; existing status wire names and reference trace method v2 remain unchanged.

Evidence and assessment schemas are now v2: the trace field is structured, subject mismatch is an
error, and conflict interpretation is stricter. V1 envelopes are rejected rather than silently given
new meaning. Keep old artifacts immutable and explicitly reconstruct v2 reports from retained
reference evaluations with appropriate realization subjects. `ClaimEvidence.Trace` is now a
`TraceEvaluation?`; the standalone reference trace format and method remain unchanged.

`TraceEvaluation` is now an evaluator-owned sealed class rather than a publicly constructible record.
Callers should obtain it from `TraceClaimEvaluator.Evaluate` or `TraceEvaluation.Reevaluate`.
The persisted trace format remains unchanged; JSON restoration is verified even when invoked
through the general serializer. A possible conflict between bounded evidence and a refutation is
still visible in their separate contributions; no new flag infers overlap without domain evidence.
