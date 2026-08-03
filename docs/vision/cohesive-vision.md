---
kind: normative
status: accepted
authority: cohesive.vision
owners: [cohesive-core]
applies_to: [cohesive]
last_verified: 2026-08-03
supersedes: []
---

# Cohesive Vision

## Purpose

Cohesive is a comprehensive family of high-level, human-oriented semantic languages for expressing
software systems end to end: entities, relations, behavior, processes, interfaces, identity,
infrastructure (storage, network, compute), operations (telemetry, SLOs, alerts), and computational
models. Compilers and interpreters provide concrete reference realizations of those languages, but
neither their current structure nor conventional code generation is the permanent center of the
project.

Its purpose is to let people express, validate, inspect, and evolve what a system means before that
meaning is committed to a particular database, runtime, framework, transport, client technology,
cloud, model provider, accelerator, or generated implementation. Agents and other tools are expected
to produce and transform most materialized IR. Compilers, agents, and interpreters attach the
semantics to concrete systems while preserving declared guarantees and retaining evidence about how
each realization was chosen.

Cohesive is a semantic orchestration layer, not a replacement technology stack. It should make existing
infrastructure more composable and replaceable by giving it explicit meaning to implement.

## The problem

Application semantics are normally distributed across code and configuration that were written for
different consumers:

- types and validation rules;
- queries, indexes, caches, and projections;
- entity methods, handlers, events, and transactions;
- workflows, retries, checkpoints, and compensations;
- routes, schemas, clients, forms, and presentation behavior;
- authorization rules and identity scopes;
- deployment resources and provider configuration;
- tests, documentation, monitoring, and migration scripts.

These artifacts frequently describe the same fact in forms that cannot inspect or verify one
another. The implementation becomes the only apparent source of truth, but its meaning must be
reconstructed from infrastructure-shaped details. Changes become expensive for both human and
automated maintainers—and ultimately for the organization—because each semantic echo must be
discovered, updated, and verified across layers.

The problem grows when infrastructure changes, behavior must run in more than one placement, or an
agent must reason about the system. Code generation alone does not solve it: generated artifacts
remain trustworthy only when the generator has a durable semantic source and preserves provenance.

## The agentic challenge

Cohesive must be clear-eyed about the bitter lesson for software abstractions: as general-purpose
models and coding agents become more capable, a traditional library or framework may become less
valuable. An agent can synthesize application-specific code directly, use the full capability of the
chosen infrastructure, and avoid constraints imposed by an external component. Increasingly capable
agents may eventually translate human specifications into working and evolving systems more
effectively than a fixed sequence of intermediate languages and conventional compiler passes.

Cohesive must not assume its abstractions are justified merely because they are reusable or
declarative. Every semantic layer must earn its place by improving at least one of the following:

- the quality with which people can express and validate intent;
- the strength and economy of verification;
- comprehension by people and agents;
- safe change across the full system lifecycle; or
- the ability to exploit target capabilities without losing declared meaning.

If an agent can produce, verify, explain, and evolve a direct implementation from human intent more
reliably and economically than Cohesive can, adding a Cohesive layer is the wrong choice for that
case.

Cohesive therefore focuses on the human-oriented language space rather than treating today's code
generators as the final product. The languages provide durable objects around which people can
calibrate intent, examine alternatives, understand consequences, and judge evidence. Human-oriented
does not mean primarily human-authored: canonical IR should be readable and navigable by people, but
agents, inference systems, importers, and other tools are expected to author and transform it.

The current compilers and reference interpreters make semantics concrete, executable, testable, and
falsifiable. They are reference implementations and verification oracles, not a claim that future
systems must lower through the same phases or emit the same kinds of code. Agent synthesis, learned
compilation, search, partial evaluation, direct execution, and mechanisms not yet anticipated may
become interpretations of the same languages. The compiler architecture should be expected to
evolve profoundly as long as the declared semantics and resulting evidence remain intelligible.

## The thesis

Cohesive makes semantics first-class, portable, persisted values. A semantic model may be produced
by a coding agent, authored through a host-language DSL, inferred by a system such as Ari, imported
from another representation, synthesized from examples or observations, or transformed by tooling.
These are producers of canonical IR, not independent semantic authorities.

Once materialized, the IR can support multiple interpretations:

- authoritative or optimistic execution;
- static validation and capability analysis;
- physical planning and optimization;
- simulation, model exploration, and deterministic replay;
- example, property, conformance, and differential testing;
- API, client, schema, UI, and infrastructure generation;
- documentation and visualization;
- migration, compatibility, cost, and security analysis; and
- runtime observability, audit, and drift detection.

The economic claim is that the modeling cost is repaid whenever another interpretation reuses the
same meaning instead of reimplementing it. The detailed adoption argument is in
[Why use Cohesive blocks?](../WHY_COHESIVE.md).

## Primary optimization objectives

Cohesive is optimized around three related questions:

1. **Validation: is the declared “right thing” actually what people intend?** Languages,
   examples, simulations, scenarios, review projections, and runtime feedback must help people
   discover ambiguity, missing requirements, unintended consequences, and disagreement before and
   after implementation.
2. **Verification: does the realized system do that thing?** Reference interpreters, static
   analysis, capability evidence, conformance suites, generated tests, differential execution,
   provenance, and runtime checks must establish whether code and infrastructure preserve the
   accepted semantics.
3. **Comprehension: can people and agents understand the system at the appropriate level of
   abstraction?** The system must explain definitions, changes, realization choices, dependencies,
   behavior, failures, and operational state without requiring every consumer to reconstruct meaning
   from generated code.

These objectives constrain one another. Verification against misunderstood intent is insufficient.
Readable intent without evidence of realization is insufficient. Correct behavior that cannot be
understood or safely changed will decay. Interpretations should therefore return evidence that can
serve all three objectives rather than only produce executable artifacts.

## Change and the full lifecycle

A software system is never finished. Cohesive treats change as a first-class concern spanning
discovery, design, implementation, verification, deployment, operation, learning, and migration.
Persisted semantic definitions are valuable because revisions and proposed changes can themselves be
inspected and interpreted.

A semantic change should support projections for:

- validation of the intended outcome and affected scenarios;
- structural and behavioral comparison with the previous revision;
- dependency and consumer impact analysis;
- capability and realization re-evaluation;
- migration of data, APIs, clients, materializations, and in-flight processes;
- generated implementation and test changes;
- staged rollout, compatibility, rollback, and reconciliation;
- updated documentation, visualization, and agent context; and
- runtime observation of whether the new revision achieved its intended effect.

The lifecycle is a feedback loop rather than a one-way compilation pipeline. Production evidence,
human corrections, incidents, evaluation results, and discovered constraints can produce new
proposals. Agents should be able to operate on explicit semantic changes and evidence instead of
reconstructing the change from a diff of unrelated generated artifacts.

## Semantic surface before infrastructure

Cohesive follows language-oriented programming. Stable semantic constructs lead; target-specific
behavior attaches through interpreters, adapters, agents, compiler configuration, and explicit
extensions.

The semantic surface must not collapse to the lowest common denominator of supported targets.
Instead, target adapters declare their capability closure: capabilities, constraints, guarantees,
limits, and attributable composition strategies. A compiler matches semantic requirements against
that evidence.

A realization may be:

- native to one target;
- composed from multiple facilities;
- valid only within a declared operating boundary;
- supplied by a local, explicit override; or
- unavailable.

No realization mechanism—including an agent—may silently weaken atomicity, consistency, isolation,
ordering, durability, recovery, precision, accessibility, or another requested guarantee. It must
either preserve the semantics or produce a precise diagnostic with resolution paths.

## Portable in more than one sense

Cohesive seeks several kinds of portability:

- **Authoring portability:** agent-produced, authored, inferred, imported, or generated models
  converge on the same canonical IR.
- **Host-language portability:** C#, TypeScript, Java, Python, Rust, and future hosts can produce or
  consume the same semantic model.
- **Target portability:** the model can be lowered to multiple storage, runtime, UI, API, AI,
  numerical, and infrastructure targets.
- **Placement portability:** suitable behavior may run on a backend, frontend, device, edge,
  database, orchestrator, or accelerator.
- **Purpose portability:** the same model supports execution and non-execution interpretations.
- **Temporal portability:** persisted revisions support evolution, replay, migration, and
  compatibility analysis for long-lived state and processes.

Portability does not mean every target realizes every model. It means support and mismatch are
explicit, inspectable, and attributable.

## One source of truth, many derived artifacts

The canonical IR is the source of semantic truth. Generated code, schemas, physical plans,
deployment resources, telemetry projections, and documentation are derived artifacts. Every derived
artifact should identify:

- the source definition and revision;
- relevant source nodes;
- the interpreter and version;
- effective compiler policy and conventions;
- target capability evidence;
- selected realization decisions; and
- explicit overrides or declared tradeoffs.

Backend-specific behavior must be represented as an extension, compiler option, target profile, or
attributable override. Editing a generated artifact must not create an invisible second model.

## Explainable conventions

Common cases should require semantic declarations rather than exhaustive infrastructure
configuration. Cohesive therefore favors convention-driven defaults for naming, target selection,
registration, lowering, artifacts, and operational behavior.

Conventions are default compiler policy, not ambient magic. Effective configuration must distinguish:

1. explicit local declarations and overrides;
2. scoped application or subsystem profiles;
3. adapter and compiler conventions; and
4. framework-wide defaults.

Every inferred value should be deterministic and explainable. A convention may choose between
semantically equivalent realizations, but it may not invent requirements or weaken guarantees.

## A family of languages

Cohesive is not intended to become one universal syntax or one monolithic IR. Different semantic
domains require different constructs and invariants. They should nevertheless share a small set of
foundational concepts such as shapes, expressions, identities, provenance, capabilities,
revisions, changes, diagnostics, configuration, and execution evidence.

Separate types and phases must correspond to a real distinction in valid states, ownership,
lifecycle, versioning, units, serialization, or guarantees. Layer boundaries alone do not justify
duplicated models. The family should continually distill recurring mechanisms while retaining the
semantic distinctions that make invalid states unrepresentable.

The ownership and composition of the languages are described in
[Language family](../architecture/language-family.md).

## Incremental adoption

Cohesive must reward partial use. A team should be able to adopt:

- core shapes and expressions without a new runtime;
- Relations as a mapper, query builder, or deterministic in-memory evaluator;
- one Transition for one behavior-rich entity;
- one Process for one coordination-heavy workflow;
- an API declaration for schema or client generation;
- one Presentation view projected into an existing application; or
- a provider-neutral AI, configuration, identity, or storage contract.

Each block should provide a local benefit. Connecting blocks should compound the return by removing
duplicated semantic descriptions, not impose suite-wide adoption as a prerequisite.

## Ari and other producers

Ari is a product and inference system that can propose Cohesive relation semantics. Ari owns
inference features, model evidence, scores, alternatives, explanations, review workflow, datasets,
and training lifecycle. Cohesive owns portable drafts, accepted definitions, validation, compiler
requirements, and target realization.

Inference uncertainty must remain distinct from an incomplete semantic definition and from a
runtime evidence gap. An inferred proposal becomes canonical only through an explicit,
shape-aware acceptance boundary. Other inference systems, importers, and authoring environments
should be able to use the same producer contracts.

## Desired properties

Cohesive should make systems:

- **semantically coherent:** one authority for each fact;
- **validatable:** people can determine whether declared meaning reflects their intent;
- **inspectable:** models and effective configuration are durable data;
- **explainable:** plans, defaults, diagnostics, and runtime decisions retain reasons;
- **verifiable:** interpreters can be compared with reference and conformance suites;
- **comprehensible:** people and agents can obtain purpose-appropriate views of meaning and behavior;
- **extensible:** target-specific capability is preserved through explicit extension points;
- **efficient:** common paths avoid hidden allocation, reflection, blocking, and redundant work;
- **change-oriented:** proposals, revisions, impact, compatibility, migration, deployment, and
  runtime learning are designed into the full lifecycle;
- **adoptable:** individual blocks work in ordinary applications; and
- **agent-native:** agents can produce and transform IR, reason from bounded semantic evidence, and
  verify their work without making opaque agent output authoritative.

## Non-goals

Cohesive is not intended to:

- replace databases, workflow engines, frontend frameworks, clouds, or model runtimes;
- hide meaningful differences between targets behind a uniform facade;
- guarantee portability by restricting semantics to universally available features;
- make every application declarative or prohibit direct infrastructure access;
- treat generated code as the source of truth;
- require people to hand-author canonical IR as the normal development workflow;
- preserve the current compiler architecture or conventional generated code as permanent machinery;
- make inference output authoritative without validation and acceptance;
- create a new abstraction for every compiler phase or target; or
- require complete adoption before providing value.

Direct access and overrides are legitimate escape hatches. They must be local, explicit,
inspectable, and attributable so that they do not silently become a second semantic system.

## What success looks like

Cohesive is succeeding when:

- a semantic change can identify affected APIs, views, processes, storage requirements, clients,
  tests, migrations, and operational signals before deployment;
- agents can propose and implement a change from human-oriented semantics while preserving an
  inspectable chain of validation, verification, and comprehension evidence;
- multiple interpreters preserve declared meaning or reject unsupported requirements precisely;
- a target can expose a specialized capability without leaking its SDK into core semantics;
- generated artifacts and runtime observations can be traced to source IR and compiler decisions;
- common adoption begins with one useful block and expands only when the next interpretation pays
  for itself;
- conformance and differential tests catch semantic drift between targets;
- humans and agents can answer what was intended, how it was realized, why it was valid, and what
  happened at runtime;
- people can read the IR or an attributable projection well enough to calibrate intent and judge the
  agent-produced result without being expected to author the normalized representation directly;
  and
- the number of independent semantic models decreases as the system grows.

This document describes direction, not a release claim. Package READMEs, executable tests, and
compatibility inventories remain the authorities for currently implemented behavior.
