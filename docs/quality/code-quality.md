---
kind: normative
status: draft
authority: cohesive.code-quality
owners: [cohesive-core]
applies_to: [cohesive]
last_verified: 2026-08-11
supersedes: []
---

# Cohesive Code Quality and Optimization Model

## Purpose

This document defines how Cohesive evaluates implementation quality when desirable properties
conflict. It governs code written by people and agents. It does not prescribe one universally
optimal representation; it defines how contributors establish which implementation is preferable
for a particular semantic and operational context.

Code production is inexpensive relative to establishing that code is correct, coherent, and
appropriate for the architecture. Generated code is therefore still architectural weight. The
default optimization target is the long-term cost of establishing, preserving, explaining, and
evolving intended semantics—not the cost of typing an implementation.

## Working definition

Optimal Cohesive code is the smallest coherent implementation that:

- faithfully preserves intended semantics;
- makes invalid or unsupported realizations explicit;
- retains one inspectable source of truth for each concept;
- satisfies demonstrated operational constraints;
- exposes enough evidence to verify and diagnose its behavior; and
- can be changed or reinterpreted without duplicating meaning.

"Smallest" refers to conceptual surface, not character, line, file, or type count. "Coherent" means
that local convenience does not take precedence over the system's semantic model.

## Quality is a contextual partial order

Quality has several dimensions that cannot always be reduced to one score. One design may be faster
but more specialized; another may be easier to replace but harder to optimize; another may prevent
more invalid states at the cost of a larger public type surface. Several designs may occupy a
Pareto frontier with no context-free winner.

When qualities conflict, apply this default priority:

1. Preserve declared semantics, invariants, security, and guarantees.
2. Satisfy explicit correctness and operating constraints.
3. Maintain one identifiable semantic authority.
4. Minimize independent concepts and change-propagation cost.
5. Make behavior verifiable, attributable, and diagnosable.
6. Satisfy measured performance requirements.
7. Optimize local elegance, brevity, and convenience.

The order may change when the domain establishes a different constraint. For example, latency,
throughput, allocation behavior, or numerical precision may be a primary requirement for an
execution kernel. A changed priority must be explicit and supported by relevant evidence; it must
not silently weaken declared meaning.

## Durable quality properties

### Semantic fidelity

An implementation must preserve the meaning and guarantees it claims to realize. A representation,
optimization, adapter limitation, or convenient fallback must not silently change atomicity,
ordering, durability, consistency, isolation, precision, authorization, recovery, or another
declared property.

### Semantic authority

Each concept should have one identifiable authority. Multiple enums, strings, switch tables,
schemas, generated contracts, or frontend models that must evolve together are evidence that
meaning has been duplicated. Prefer projection or generation from the authority.

Syntactic duplication is not automatically harmful. Similar code may represent intentionally
different target policies. Conversely, mechanically dissimilar code can still duplicate the same
semantic decision. Optimize the number of independently maintained facts, not the superficial
amount of repetition.

### Conceptual economy

Every type, abstraction, layer, extension point, and configuration surface adds a concept that
future contributors must understand. Add one when it prevents an invalid state, establishes an
ownership or versioning boundary, captures a stable recurring mechanism, or removes more ambiguity
than it creates.

Do not generalize solely because future divergence is imaginable. Prefer an implementation that is
easy to split when a concrete differing invariant appears over a framework parameterized for
hypothetical variation.

### Understandability and agent legibility

Code should make intent, invariants, authority, ownership, failure behavior, and important costs
discoverable. Human and agent readers should be able to identify why the design has its current
shape and what evidence supports it.

Mechanics can often be explained from code on demand. Documentation should concentrate on facts
that syntax cannot reliably recover: decisions, invariants, semantic distinctions, operating
boundaries, rejected alternatives, ownership, and failure contracts.

### Changeability and reversibility

Prefer choices that localize the propagation of semantic change and keep uncertain decisions
reversible. Extensibility should follow demonstrated variation rather than maximize the number of
possible substitutions. Public extension points require clearer invariants and stronger evidence
than internal seams.

### Verifiability

Important claims should have a cheap and decisive way to test whether they remain true. Favor
executable invariants, property tests, differential tests, reference interpreters, conformance
suites, deterministic outputs, and attributable diagnostics over tests that only repeat examples.

Passing tests is necessary evidence, not a complete design argument. Tests may fail to exercise the
relevant invariant or may preserve two equally duplicated models in synchronization.

### Performance and operational fitness

Performance is part of quality when it affects an explicit operating envelope. The common path
should not incur avoidable abstraction overhead, and foundational designs should not foreclose
later optimization where change would be prohibitively expensive. Nontrivial optimization should
otherwise follow representative measurement.

Prefer clear, intent-revealing code before a path is known to be performance-sensitive, but preserve
**optimization latitude**: keep operation semantics, data ownership, evaluation boundaries, and
side effects explicit enough that a more efficient realization can replace the initial one without
changing declared behavior or unrelated callers. This is different from speculative optimization.
It preserves the ability to optimize without paying the complexity cost in advance.

For example, LINQ may be the clearest way to express a transformation on a non-hot path. The design
should not prohibit it preemptively, but it should avoid entangling that query with ambient state or
leaking incidental materialization assumptions. If measurement later identifies the path as hot,
the coherent operation can be replaced by a fused loop, span-based implementation, batching, or
another target-appropriate realization and verified against the same semantic fixtures. Do not
introduce an abstraction solely to create a hypothetical optimization seam; the seam should align
with an actual semantic operation or ownership boundary.

Performance work must identify the semantic fixture, workload, environment, relevant resource
dimensions, and before-and-after result. An optimization that changes observable meaning is a
semantic change, not merely a faster implementation. See [Conformance](conformance.md#performance-conformance)
for benchmark and evidence requirements.

## Metrics are sensors, not objectives

Cyclomatic complexity, coverage, allocation counts, dependency counts, coupling, and line counts
can reveal locations worth investigating. None can establish architectural quality alone.

For example, splitting an exhaustive switch into a class hierarchy may reduce a complexity score
while scattering a closed semantic set. Removing repeated syntax may create a parameterized
abstraction that hides genuine target differences. Increasing coverage may add assertions that do
not challenge an invariant. Metrics should initiate a semantic investigation rather than dictate
its conclusion.

## Required decision protocol

### Before implementation

For a nontrivial change:

1. Identify the semantic authority and affected invariants.
2. Search the core, prelude, sibling blocks, and sibling targets for the same concept or mechanism.
3. Identify explicit compatibility, security, performance, and operational constraints.
4. Distinguish present requirements from hypothetical future variation.
5. Identify likely change axes and the cost of reversing the decision.
6. Determine what evidence can establish correctness and fitness.

### While choosing a design

When viable alternatives differ materially:

1. State the non-negotiable constraints.
2. Compare only the dimensions that can affect the decision.
3. Prefer the design with the lowest conceptual and change-propagation cost among those satisfying
   the constraints.
4. Prefer explicit uncertainty to an unsupported assumption.
5. Request direction when the priority depends on product or architectural intent unavailable in
   repository authorities.

Routine changes do not require a ceremonial alternatives analysis. The comparison should be
proportional to the semantic reach, irreversibility, and operational risk of the decision.

### Completion audit

Before completing a nontrivial change, answer:

- **Semantic audit:** Are intended semantics and guarantees preserved?
- **Authority audit:** Was another source of truth introduced?
- **Abstraction audit:** Is stable shared mechanism centralized at the lowest layer that owns it?
- **Type audit:** Does each new type enforce a meaningful distinction?
- **Performance audit:** Are important costs acceptable and measured when performance influenced
  the design?
- **Verification audit:** Does the evidence exercise invariants and relevant failure paths?
- **Explainability audit:** Can a future contributor identify the authority, rationale, and material
  consequences?

Report material tradeoffs, evidence, and intentionally deferred work in the pull request. Do not
manufacture concerns merely to fill a template.

## Local optimization regimes

Subdirectories and packages may define local `AGENTS.md` guidance when their priorities materially
differ from the repository default. Local guidance should state the changed ordering and the
evidence required. Examples include:

- semantic IR prioritizing durability, portability, and minimal independent concepts;
- compiler planning prioritizing determinism, provenance, and precise diagnostics;
- execution kernels prioritizing measured latency, throughput, locality, and allocation behavior;
- adapters prioritizing preservation of target capabilities and guarantees; and
- samples prioritizing clarity and representative use over generality.

Local policy refines this model; it does not silently waive semantic fidelity or create a second
authority.

## Enforcement boundaries

Automate mechanically decidable constraints such as formatting, compilation, documentation
coverage, deterministic generation, dependency direction, conformance, and stable benchmark
thresholds. Use review and explicit design reasoning for semantic appropriateness, abstraction
quality, and tradeoffs that cannot be inferred from a metric.

The division of responsibility is:

- `AGENTS.md` supplies the mandatory decision protocol and repository defaults.
- This document supplies the rationale and normative quality model.
- Local `AGENTS.md` files may define explicit subsystem priorities.
- Tests, analyzers, and CI establish mechanically verifiable evidence.
- Pull requests record material judgment and evidence.
- Decision records preserve exceptional or durable architectural choices.
