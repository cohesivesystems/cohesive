---
kind: normative
status: accepted
authority: cohesive.conformance
owners: [cohesive-core]
applies_to: [cohesive, adapters, interpreters]
last_verified: 2026-08-28
supersedes: []
---

# Cohesive Conformance Strategy

## Purpose

Conformance establishes whether a producer, interpreter, compiler, adapter, serializer, or generated
artifact preserves the Cohesive semantics it claims to support. It turns target capability claims
into executable evidence and detects drift between multiple interpretations of the same canonical
IR.

Conformance primarily serves verification: whether a realization does the declared thing. It cannot
by itself validate that the declared thing is what people intend. Conformance suites should consume
validated examples and scenarios and return diagnostics, counterexamples, provenance, and explain
evidence that support comprehension by people and agents.

Conformance does not mean that every target supports every semantic construct. A conformant target
declares a precise capability closure, preserves the semantics within that closure, and rejects
unsupported demands with attributable diagnostics.

## Principles

1. **Test claims, not target names.** A target passes the suites for the capabilities and guarantees
   it declares.
2. **Use canonical IR as input.** Do not reconstruct semantics independently inside every adapter
   test.
3. **Compare observable semantic results.** Provider query text or internal plans are secondary
   evidence unless their exact form is a durable contract.
4. **Keep a reference interpretation.** It establishes clear executable behavior within a declared,
   often narrower, capability closure.
5. **Reject rather than weaken.** Unsupported required behavior must produce stable structured
   diagnostics before partial execution where practical.
6. **Exercise composition.** A composed realization must pass the same semantic cases as a native
   realization.
7. **Retain provenance.** A conformance result identifies definitions, interpreters, capability
   profiles, policies, fixtures, and versions.
8. **Separate correctness from performance.** A semantically correct realization may still fail a
   separately declared operating boundary or service-level requirement.
9. **Use shared fixtures for shared contracts.** Keep target-specific tests for genuine target
   behavior, not copies of universal invariants.
10. **Treat diagnostics and explain output as product surfaces.** Their codes, attribution, and
    resolution information are testable contracts.

## Conformance claim

An interpreter or adapter conformance claim should identify:

- semantic language and IR versions;
- supported node and expression kinds;
- supported semantic options and guarantees;
- constraints and operating boundaries;
- unsupported or partially supported capabilities;
- interpreter or adapter version;
- target version or profile where relevant;
- applicable conformance-suite version; and
- evidence from the most recent verified run.

Avoid a single `SupportsRelations` or `IsConformant` flag. Capability profiles should be granular
enough for the compiler to match actual requirements and for tests to select the relevant cases.

## Verification layers

### 1. Semantic model tests

Fine-grained unit and property tests verify each language independently:

- construction and invalid-state prevention;
- semantic validation and diagnostic attribution;
- deterministic canonicalization and fingerprinting;
- identity and revision behavior;
- expression typing and value semantics;
- requirement and dependency analysis;
- provenance retention; and
- change and compatibility classification.

These tests do not require concrete infrastructure.

### 2. Reference interpreter tests

A reference interpreter provides straightforward executable semantics for a declared capability
closure. Reference tests emphasize clarity, determinism, edge cases, and diagnostic quality over
target-specific optimization.

The reference interpreter must publish its own limits. It must not accidentally become a universal
specification by failing to implement semantics the IR validly expresses. Normative documents and
language validators remain authorities beyond the reference closure.

### 3. Shared conformance fixtures

Reusable fixtures contain canonical definitions, inputs, expected semantic results, expected
diagnostics, and provenance assertions. Each relevant interpreter runs the same fixtures.

A fixture should identify:

- semantic requirement IDs and source nodes under test;
- required capability profile;
- valid input domain and observations;
- expected outputs, outcomes, completeness, ordering, or effects;
- permitted nondeterminism, if any;
- expected failure class and diagnostic code for unsupported cases; and
- sensitive or target-specific values that must be normalized before comparison.

Fixtures should be organized by semantic feature, not by target. Target projects may add cases for
provider limits, serialization, resource cleanup, and operational behavior.

### 4. Adapter integration tests

Local integration tests run real adapter logic against an isolated target, emulator, container, or
faithful test implementation. They verify:

- target construction and serialization;
- binding and input acquisition;
- boundary conversions;
- transaction and recovery behavior;
- cancellation and resource cleanup;
- provider error normalization;
- capability-limit diagnostics; and
- observable results against shared fixtures.

Mocks are appropriate for component interaction but cannot establish a provider's claimed
transaction, ordering, query, or recovery guarantee. Production-shaped vertical slices should be
available on demand for important adapters.

### 5. Differential tests

Differential tests execute one canonical definition through two or more interpretations and compare
normalized semantic observations. Useful pairs include:

- reference interpreter versus target adapter;
- native target lowering versus composed lowering;
- old interpreter version versus new version;
- backend authoritative transition versus frontend optimistic subset;
- generated client versus server binding;
- local process runtime versus durable runtime; and
- model or numerical backend versus another backend within shared precision rules.

Comparison should cover more than the main return value. Depending on the language it may include
ordering, multiplicity, completeness, lineage, decisions, patches, effects, retry obligations,
diagnostics, provenance, and explain decisions.

When exact equality is inappropriate, the semantic model must define the comparison: tolerance,
partial order, allowed result set, observational equivalence, or declared nondeterminism.

Durable canonical serialization requires a differential suite whenever more than one writer, serializer, or
streaming strategy realizes the same format. All strategies must consume the same fixtures, and those fixtures must
exhaust the semantic value-kind catalog so a newly added kind cannot silently remain unsupported. Verification must
include exact byte equality for property ordering, scalar normalization, escaping, binary encoding, and nested
structures; generated combinations and bounded-large tokens should supplement explicit boundary cases. Rejection
behavior must agree on the failure class for values or policies outside the canonical domain. A performance-specific
writer is a separate physical realization, not a separate authority for the wire format.

### 6. End-to-end golden verticals

The scenarios in [Golden verticals](../use-cases/golden-verticals.md) verify composition across
languages and generated boundaries. They catch ownership drift that package-local tests cannot,
such as duplicated identifiers, incompatible outcomes, missing provenance, or target policy leaking
into a semantic definition.

End-to-end suites should remain small and durable. Fine-grained behavior belongs lower in the test
pyramid.

The [Execution Kernel adoption guide](../EXECUTION_KERNEL_GUIDE.md) identifies a curated executable
suite over the existing canonical Transition, durable Process, Motion DQ, index-sync, and API/CLI
contracts. Run it without a separate tutorial runtime or example semantic model:

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj \
  --filter 'Category=ExecutionKernelExample'
```

This category is a documentation and adoption projection of the source-backed tests. It does not
replace the validators, compatibility scenarios, reference interpreters, or adapter conformance
evidence that remain authoritative for their respective claims.

### 7. Runtime conformance and drift

Runtime evidence can validate that a deployed realization continues to operate within its declared
boundary. Examples include:

- realization and definition fingerprints on traces;
- latency, batch-size, cardinality, freshness, and resource-bound observations;
- unexpected fallback or override detection;
- comparison of sampled target results with a reference interpreter;
- materialization and index reconciliation;
- replay of recorded process decisions; and
- deployed artifact provenance verification.

Runtime observations do not prove all possible behavior, but they detect environmental drift and
incorrect capability assumptions that static testing cannot see.

## Language-specific obligations

### Shapes and expressions

Conformance covers field identity, presence, nullability, cardinality, conversions, scalar value
semantics, collection behavior, ordering, numeric precision, overflow, and error classification.
Expression interpreters must test every declared operator and function across supported input
domains and reject missing capability before silently evaluating a different operation.

### Relations

Conformance covers correlation, traversal, join behavior, projection, filtering, aggregation,
ordering, pagination, temporal membership, multiplicity, completeness, missing evidence, lineage,
dependency analysis, and runtime value semantics.

Physical partitioning, source ordering, acquisition batch size, and native-versus-composed strategy
should vary without changing the logical result. Adapter-specific query text may be asserted when
necessary to prove capability use or prevent unsafe construction, but it is not a substitute for
semantic result tests.

### Transitions

Conformance covers required observations, preconditions, branch selection, rejection, sparse patch,
outcomes, effects, invariants, version checks, atomic commit, idempotency, retry safety, and outbox
obligations.

Property and state-machine tests should generate valid entity states and sequences of inputs.
Storage-adapter tests must establish the guarantees they claim across conflicts and failure
boundaries, not only the pure decision result.

### Processes

Conformance covers control flow, continuation, replay determinism, signal and timeout arbitration,
retry, cancellation, pause and resume, idempotent operations, parallelism, compensation, revision
binding, checkpoint atomicity, and terminal outcomes.

Failure injection should cover crashes before and after each durable boundary. The test oracle must
compare logical obligations and outcomes rather than vendor checkpoint representation.

### API

Conformance covers operation identity, route and parameter projection, input/output/problem shapes,
pagination, scope policy, serialization, generated descriptions, server binding, clients, and
compatibility classification.

Generated OpenAPI, GraphQL, TypeScript, and server artifacts should all trace to the same operation
and shape revisions. Contract tests should ensure consumers and providers agree without turning a
consumer's accidental implementation detail into universal semantics.

### Presentation

Conformance covers navigation, views, fields, actions, forms, flows, component roles, design intent,
data and operation bindings, accessibility, residency, generated identifiers, and semantic
automation selectors.

Renderer tests should assert semantic behavior through stable presentation identifiers. Avoid
brittle snapshots of incidental DOM or styling. Visual tests may supplement semantic assertions for
layout and design-system fidelity, but they do not replace interaction and accessibility checks.

### Identity

Conformance covers principal resolution, scope placement, delegation, claims normalization,
fail-closed behavior, authorization context, and provenance. Provider tests must cover malformed,
missing, ambiguous, expired, and cross-tenant identity evidence without logging sensitive values.

### AI and numerical interpretations

Conformance separates deterministic contract checks from probabilistic quality evaluation:

- shapes, tensor ranks, data types, devices, and unsupported operations are deterministic contracts;
- numerical results use explicitly declared precision and tolerance rules;
- training reproducibility records code, data, seed, configuration, environment, and model lineage;
- model quality uses versioned datasets, stable splits, slice metrics, baselines, and promotion
  thresholds;
- inference confidence is calibrated evidence, not a semantic guarantee; and
- nondeterministic or stochastic behavior declares repeat counts and statistical acceptance rules.

For Ari-produced drafts, Cohesive conformance begins at deterministic draft validation and
acceptance. Ari separately evaluates candidate recall, ranking, calibration, abstention, ambiguity,
and review usefulness.

## Capability declarations and negative tests

Every positive capability claim should have:

- at least one shared conformance case demonstrating it;
- boundary cases for declared constraints;
- a negative test showing the compiler rejects the nearest unsupported demand; and
- explain evidence showing why the capability matched.

Every unsupported required capability should result in a structured diagnostic containing:

- stable code and severity;
- semantic requirement and source node;
- target and missing or conflicting capability;
- relevant constraint or operating boundary;
- selected policy and rejected strategies where useful; and
- actionable resolution paths such as changing policy, adding infrastructure, narrowing the declared
  boundary, selecting another target, or introducing an explicit override.

Diagnostics should be tested structurally. Exact prose may evolve unless it is itself a published
contract, but remediation information must remain useful to humans and agents.

## Serialization and fingerprint conformance

Persisted IR and durable artifacts require dedicated compatibility tests:

- canonical round trips preserve semantic identity and content;
- serializers reject invalid and unknown required constructs;
- canonical order and number formatting are deterministic;
- fingerprints change for semantic changes and remain stable for incidental representation changes;
- old fixtures remain readable for every claimed supported version;
- migrations produce expected revisions with source provenance; and
- independently implemented serializers agree on canonical fixtures where host portability is
  claimed.

Golden serialized fixtures are appropriate for durable wire formats. They should be minimal and
reviewed as contracts rather than used as broad snapshots of incidental output.

## Generated-artifact conformance

Generators should be tested for:

- deterministic output from identical inputs and tool versions;
- complete provenance headers or manifests;
- stable generated identities;
- valid syntax and successful target compilation;
- agreement with source shapes, routes, actions, selectors, roles, and outcomes;
- absence of hand-maintained parallel catalogs; and
- clear diagnostics when target language or framework constraints prevent faithful projection.

Generated files should normally be regenerated in verification rather than manually edited. A diff
after regeneration is evidence of drift.

## Explain and provenance conformance

Explain output and provenance must answer the claims they support. Tests should verify that:

- every physical stage maps to semantic requirements and source nodes;
- every convention-derived value names the convention or profile;
- explicit overrides are visible and scoped;
- capability matches identify the evidence used;
- generated artifacts identify definitions, revisions, fingerprints, and generator versions;
- runtime observations identify definition and realization where available; and
- sensitive values are omitted, redacted, or referenced according to policy.

An interpretation that produces a correct value without attributable decisions may still be
incomplete for production use when explainability is part of the contract.

## Performance conformance

Performance is evaluated only after semantic correctness. Nontrivial optimization requires
representative before-and-after measurements and allocation, GC, CPU, contention, I/O, and tail
latency evidence as appropriate.

Benchmarks should:

- use realistic shapes, cardinalities, and target behavior;
- separate cold compilation from warm execution;
- identify interpreter, target, hardware, runtime, and configuration versions;
- preserve the same semantic fixtures across compared implementations;
- include allocation measurements for hot paths;
- test bounded worst cases as well as typical inputs; and
- record results in a durable benchmark report when they justify architectural choices.

An optimization that changes observable semantics is a semantic change, not a performance result.

## Conformance result artifact

A durable result should contain or reference:

```text
ConformanceResult
  suite identity and version
  semantic language and IR version
  interpreter or adapter identity and version
  target profile and version
  declared capabilities and constraints
  compiler policy profile
  fixture identities and fingerprints
  passed, failed, skipped, and unsupported cases
  structured diagnostics
  normalized semantic observations
  environment and timestamp
  source revision and build provenance
```

Skipped cases must state why they are outside the declared capability closure. A target must not gain
a capability claim merely because the corresponding test was skipped.

## Promotion policy

A capability may be advertised as supported when:

1. its semantic contract and capability vocabulary are documented;
2. the interpreter declares applicable constraints and guarantees;
3. shared positive and negative conformance cases pass;
4. target integration tests establish provider-specific boundaries;
5. diagnostics and provenance meet the contract; and
6. important known gaps are published beside the claim.

Reference, experimental, preview, and stable profiles may apply different operational gates, but
they must not use different meanings for the same semantic term. Early R&D may break compatibility;
it may not silently weaken semantics.

## Review checklist

Before completing a new interpreter or adapter capability, verify:

- [ ] The semantic authority and required capability are identified.
- [ ] No duplicate semantic enum, type, or switch catalog was introduced.
- [ ] The capability declaration includes constraints and guarantees.
- [ ] Shared conformance fixtures cover success, boundary, and rejection behavior.
- [ ] Differential comparison includes all relevant observable evidence.
- [ ] Diagnostics identify source requirements and resolution paths.
- [ ] Explain output and provenance identify compiler decisions.
- [ ] Resource cleanup, cancellation, and failure boundaries are tested.
- [ ] Performance claims have representative measurements.
- [ ] Package documentation states the implemented closure and remaining gaps.
- [ ] At least one golden vertical remains coherent after the change.
