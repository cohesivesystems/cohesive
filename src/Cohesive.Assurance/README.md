# Cohesive Assurance

This first implementation evaluates **effect uniqueness**: at most one distinct committed effect for each exact operation occurrence in a declared authority/subject scope. It is a narrow A0/A1/A2 contribution to the [Assurance vision](https://app.notion.com/p/cohesivesystems/Cohesive-Assurance-Vision-3ec8cf7881f9811a8ff9c8ac46ea1172), not the full assertion language or an assurance policy engine.

The package depends only on core `Cohesive`. Storage remains authoritative for receipts and operation identities; Simulation remains authoritative for generation, shrinking, and replay. Native observation bindings stay outside this package.

## Semantics first

Read [A0 effect uniqueness semantics](docs/a0-effect-uniqueness.md). The conformance fixture table in [effect-uniqueness.json](../Cohesive.Assurance.Tests/Fixtures/effect-uniqueness.json) precedes the evaluator and defines the admitted profile.

- Repeated observations of one durable effect count once.
- Distinct durable effects for one exact operation violate the claim.
- A non-violating open prefix is **inconclusive**, not satisfied.
- Satisfaction requires explicit complete closure of the declared scope; ending a test is insufficient.
- Missing identity evidence is inconclusive unless other consistent observations establish a violation.
- Contradictory effect ownership or a foreign transition invalidates the evidence with structured diagnostics.
- Vacuous satisfaction and observed coverage are separate facts. No readiness or global proof state is inferred.

This is a finite set uniqueness primitive, not a second scalar expression language. General predicates must reuse core `Expr` when introduced. Temporal/Boolean composition, quantifier bindings, incremental monitoring, and formal verification are not implemented.

## Author and evaluate

Construct an `EffectUniquenessClaim` with a stable ID, revision, and existing `ExecutionDefinitionReference`. Bind an `EffectObservationContract` describing the exact source mapping and selected authority/subject. Assemble an `EffectUniquenessCheck` with the observed transition reference, pinned target, explicit completion, and immutable observations.

```csharp
EffectUniquenessEvidence evidence = EffectUniquenessEvaluator.Evaluate(check);
Console.WriteLine(evidence.Explain());

string retained = EffectUniquenessDocument.Serialize(check);
EffectUniquenessEvidence restored = EffectUniquenessDocument.Restore(retained);
```

The evidence retains the immutable input, coverage, verdict, diagnostics, and a pair of source observation indices when a violation exists. Retained documents pin the semantic profile and evaluator version, canonical input fingerprint, target, and mapping. Restore validates the document and recomputes the verdict; it never trusts a persisted boolean result. Unknown/duplicate/missing JSON members, mismatched fingerprints, and unsupported versions fail explicitly.

Fingerprints establish content identity, not authenticity or permission to reuse evidence. An adapter must correctly identify actual durable effects. Counting returned `Committed` statuses, using an operation ID as an effect ID, or treating a receipt as proof that no other write occurred can conceal a violation. General reuse/impact analysis and a multi-method evidence envelope remain follow-on work; this document is deliberately one versioned evidence profile.

## Verification

```sh
dotnet test src/Cohesive.Assurance.Tests -c Release
```

The focused suite includes normative fixture verdicts, an expect test, strict persistence checks, exhaustive small sequence conformance, and the existing Simulation generator/shrinker. A defective test model creates a fresh effect for every retry; it shrinks to two attempts and replays through the same claim evaluator.

An integration fixture exercises `InMemoryEntityOutboxRepository` and the existing commit protocol: commit, withhold the acknowledgement, retry the exact commit, recover the retained receipt, and check authoritative state. It reuses `RunControlFixture`, not a new reservation implementation. Its version-to-effect mapping is limited to that isolated monotonic fixture and is not a production adapter. This is implementation-candidate evidence, distinct from generated reference-model evidence.

The [standalone reservation demo](../../eng/examples/replay-assurance/README.md) now runs the existing reservation transition against a disposable PostgreSQL instance. Its native regression recreates the client after acknowledgement loss and checks an independent transaction-local stock audit; an unprotected retry is the negative control. Run `./eng/examples/replay-assurance/run-local.sh` for tests and retained evidence. Native tests explicitly skip when their database setting is absent.

A general Simulation trace bridge, production monitoring, and external verification adapters remain subsequent increments. Aspire/CQRS composition and storage semantics are unchanged.

## Design boundaries

`EffectUniquenessClaim` owns the assertion; `EffectObservationContract` owns projection and scope; `AssuranceTarget` distinguishes models, candidates, and deployments; `EffectUniquenessCheck` retains immutable source evidence; the evaluator alone owns verdict derivation. The document class centralizes canonical persistence using existing `StrictDocumentJson` primitives. No second operation-identity or capability catalog is introduced.

Evaluation takes expected linear time and memory in the number of observations and distinct effects. This is an opt-in bounded batch interpretation, not a production commit-path hook. Future streamed monitoring must preserve these fixture verdicts without inheriting unbounded batch retention.
