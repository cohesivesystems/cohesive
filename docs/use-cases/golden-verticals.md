---
kind: explanatory
status: accepted
authority: cohesive.golden-verticals
owners: [cohesive-core]
applies_to: [cohesive, ari]
last_verified: 2026-08-03
supersedes: []
---

# Cohesive Golden Verticals

## Purpose

Golden verticals are durable, end-to-end examples used to test whether the Cohesive languages form
one coherent system. A vertical begins with domain intent, crosses several semantic languages, and
ends with derived artifacts, execution, verification, and runtime evidence.

They are architectural acceptance scenarios, not a claim that every described interpretation is
implemented. Each vertical distinguishes:

- the **semantic contract** Cohesive intends to preserve;
- the **reference evidence** that should make the contract testable; and
- the **implementation closure** that package documentation and tests currently establish.

A new abstraction should simplify or strengthen at least one vertical. A change that makes every
vertical harder to explain is evidence that ownership or boundaries may be wrong.

## Common evidence bundle

Each completed vertical should eventually produce an inspectable bundle containing:

- canonical source definitions, revisions, and fingerprints;
- validation and acceptance findings;
- semantic requirement and dependency manifests;
- effective compiler configuration;
- target capability evidence;
- an attributed realization or precise rejection;
- generated artifacts with source provenance;
- reference and target interpreter results;
- conformance or differential-test results;
- runtime traces or audit evidence using semantic identities; and
- an explain projection understandable without reading provider code.

## Vertical 1: Enriched load search across placements

### User outcome

An operations user searches loads and receives a stable `LoadSearch` projection containing load
identity, customer identity, customer name, status, and selected operational facts. Loads and
customers may reside in one database or in different systems. The logical result must not change
when physical placement changes.

### Semantic declaration

Shapes describe `Load`, `Customer`, and `LoadSearch`. A relationship declares that
`Load.CustomerId` refers to `Customer.Id`. A relation query declares:

```text
Source(Load as load)
→ TraverseRelationship(load.CustomerId → Customer as customer, join: Left)
→ Filter(load.Status is within the requested status set)
→ Project(
    LoadSearch.Id           = load.Id,
    LoadSearch.CustomerId   = load.CustomerId,
    LoadSearch.CustomerName = customer.Name,
    LoadSearch.Status       = load.Status)
```

The relation owns correlation, left-join absence behavior, field demand, completeness, nullability,
multiplicity, ordering, and pagination semantics. It does not declare PostgreSQL joins,
Elasticsearch clauses, Cosmos SQL, batch sizes, or cache topology.

### Possible realizations

- A PostgreSQL adapter performs a native join and projection.
- A Cosmos DB adapter performs bounded source acquisition followed by a composed lookup strategy.
- A federated planner acquires loads from one source, batches customer keys against another, and
  correlates them through the reference interpreter.
- An Elasticsearch materialization serves the query after an index lifecycle projection has
  established freshness and rebuild semantics.
- An in-memory interpreter evaluates supplied observations for deterministic tests.

Every realization must preserve declared join, completeness, temporal, ordering, and value
semantics. If a target cannot distinguish a conclusive absence from incomplete evidence, it must
reject the demanded outer-join semantics or operate within an explicit boundary that makes the
evidence complete.

### Validation and verification

- Shape-aware validation checks all paths, conversions, presence, and nullability.
- Requirement analysis identifies exact fields and relationship edges.
- Reference fixtures exercise zero, one, and multiple matches; missing customers; null keys;
  incomplete sources; ordering; pagination; and temporal boundaries where declared.
- Each adapter runs the same conformance cases inside its claimed capability closure.
- Differential tests compare canonical rows, completeness, diagnostics, lineage, and ordering with
  the reference interpreter.
- Property tests vary source partitioning and batch boundaries without changing logical results.

### Comprehension

An explain projection answers which fields were demanded, where each output field originated, which
join strategy was selected, why the strategy preserved semantics, what target constraints applied,
and which incomplete evidence affected a result.

### Current implementation anchor

`Cohesive.Relations` already supplies canonical relation/query definitions, requirement analysis,
lineage, dependency manifests, explain projections, an in-memory interpreter, physical planning,
and concrete target work. Its package README and tests define the exact supported expression and
adapter closure. Richer targets and operational lifecycle remain incremental interpretations.

## Vertical 2: Assign a carrier through one transition policy

### User outcome

An authorized planner assigns a carrier to a load. The assignment is accepted only when the load is
in an assignable state, the carrier is eligible, and concurrency has not invalidated the decision.
All consumers observe one decision and one attributable set of effects.

### Semantic declaration

A Transition definition such as `Load.AssignCarrier` declares:

- input: load identity, carrier identity, and any required expected revision;
- state observations: current load status, existing carrier, and relevant eligibility facts;
- preconditions and explicit rejection outcomes;
- the sparse state patch for an accepted assignment;
- domain outcomes and emitted effects;
- atomicity, concurrency, idempotency, and outbox requirements; and
- stable identities for the definition, nodes, inputs, outcomes, and effects.

Carrier eligibility may be obtained through a referenced Relation query. Identity policy determines
who may request the transition. Neither the API handler nor the UI owns the transition's decision
tree.

### Interpretations

- A reference interpreter evaluates the decision against supplied observations.
- A storage compiler determines exact reads and a valid atomic commit strategy.
- An API interpreter projects an operation with generated input, outcome, and diagnostic contracts.
- A Presentation interpreter binds an action and form to stable semantic identities.
- A frontend projection evaluates a safe subset for conservative enablement or optimistic feedback
  while the backend remains authoritative.
- Documentation and visualization projections show reads, branches, writes, outcomes, and effects.
- Observability emits the transition revision, selected path, rejection reason, consulted evidence,
  changed fields, and committed effects without exposing sensitive values by default.

### Validation and verification

- Static analysis proves referenced fields and expressions are well-typed.
- Example tests cover accepted and rejected business paths.
- Property tests preserve entity invariants across generated valid states and inputs.
- State-machine tests explore repeated, conflicting, and reordered requests.
- Storage adapters demonstrate that decision, patch, version, and required effects commit with the
  declared guarantees.
- API and Presentation contract tests verify that generated identities and outcomes remain aligned.
- Differential tests compare reference and target decisions rather than only comparing exceptions or
  serialized responses.

### Comprehension

A support engineer should be able to answer why an assignment was rejected, which revision ran,
which facts influenced the decision, whether an optimistic frontend result differed from the
authoritative result, and which adapter guarantee supported the commit.

### Current implementation anchor

Canonical Transition IR and reference execution are active areas of implementation. Consult the
[Execution Kernel compatibility inventory](../EXECUTION_KERNEL_COMPATIBILITY.md) and the
[Transitions package documentation](../../src/Cohesive.Transitions/README.md) before treating the
complete lifecycle above as shipped behavior.

## Vertical 3: Durable tender and carrier-response process

### User outcome

A load is tendered to a carrier, waits for a response until a deadline, handles acceptance,
rejection, timeout, cancellation, or duplicate messages, and either completes or selects a declared
recovery path. The logical process remains stable when executed locally or through a durable
orchestrator.

### Semantic declaration

The Process definition coordinates semantic operations by identity:

1. evaluate load and carrier eligibility;
2. invoke the transition that records the tender attempt;
3. emit an idempotent carrier request;
4. wait for a correlated response or deadline;
5. arbitrate signal, timeout, cancellation, and control operations deterministically;
6. invoke the accepted, rejected, or timed-out transition;
7. retry, continue to another carrier, compensate, or complete according to declared policy; and
8. expose stable lifecycle and outcome evidence.

The Process owns control flow, correlation, durable continuation, retry and timeout meaning,
idempotency requirements, compensation, and outcome semantics. It does not copy the internal
eligibility query or state-change policy.

### Possible realizations

- An in-memory runtime supports fast deterministic tests and simulation.
- The accepted Durable Task target interprets the same exact compiled plan through native and composed
  Scheduler facilities within an explicit capability closure; it is not implemented yet.
- A future Temporal or custom runtime adapter maps the same nodes and obligations to a different
  physical substrate.
- A simulation interpreter explores message order, retry schedules, failures, and recovery without
  performing external effects.
- A visualization interpreter projects the process graph and live progress.

### Validation and verification

- Graph validation rejects invalid references, illegal cycles, unreachable required nodes, and
  incomplete terminal behavior.
- Determinism tests vary replay and activation boundaries.
- State-machine and model-based tests vary response, timeout, cancellation, pause, resume, and
  duplicate-delivery ordering.
- Crash-boundary tests verify atomic ownership of checkpoint, durable-operation, interaction,
  control, and compensation state.
- Adapter conformance tests compare observable process outcomes and obligations with the reference
  runtime.
- Long-lived revision tests define how in-flight instances bind to or migrate between definition
  revisions.

### Comprehension

An operator can see the logical node, continuation, pending obligations, correlation identity,
attempts, deadlines, controls, compensation status, definition revision, selected runtime, and the
reason the process is waiting or terminal.

### Current implementation anchor

Processes, the reference interpreter, native durable runtime, durable-operation contracts, and distribution
semantics implement substantial subsets of this lifecycle. Durable Task execution is accepted future direction;
the current package provides historical monitoring only. Advanced control, durable signal arbitration,
parallelism, compensation, migration, and every target adapter claim must be checked against the
[Execution Kernel compatibility inventory](../EXECUTION_KERNEL_COMPATIBILITY.md) and the accepted
[Durable Task interpreter decision](../decisions/durable-task-process-interpreter.md).

## Vertical 4: One semantic operation from backend to user interface

### User outcome

A user opens a load workspace, sees an enriched load view, performs the carrier-assignment action,
receives an attributable outcome, and sees affected data refresh. Routes, inputs, outcomes,
selectors, authorization, and UI behavior do not drift across backend and frontend implementations.

### Semantic composition

- Shapes own load, carrier, assignment input, result, and problem contracts.
- Relations own the workspace data query.
- Transitions own assignment behavior.
- API binds the query and transition to externally accessible operations.
- Identity supplies principal and scope requirements.
- Presentation declares the workspace, view, data source, form, action, flow, accessibility
  expectations, and stable automation selectors.
- Code generation projects TypeScript types, clients, constants, test mocks, and other required
  frontend contracts.
- Framework adapters render the model through React, Blazor, or another target.

Presentation and API refer to canonical semantic identities. They do not repeat domain outcomes or
route strings in independently maintained catalogs.

### Validation and verification

- Composition validation ensures every referenced definition and generated contract exists.
- Authorization analysis checks that exposed actions and data sources have compatible scope policy.
- Accessibility validation checks the declared interaction contract independently of a renderer.
- API conformance tests validate request, response, problem, and pagination projections.
- Frontend type/build tests consume generated contracts.
- UI automation uses semantic selectors such as `data-presentation-view-id` and
  `data-presentation-action-id` rather than CSS structure.
- Renderer conformance exercises semantic actions, forms, outcomes, focus, and navigation without
  relying on brittle visual snapshots.

### Comprehension

Impact analysis can walk from a changed Transition outcome to its API schema, generated client,
Presentation action, process consumer, tests, and runtime signals. A UI failure can be related back
to the semantic action and operation rather than only to a DOM selector or route.

### Current implementation anchor

Cohesive contains API and Presentation definitions, code generation, ASP.NET integration, frontend
packages, and semantic test selectors. Their package READMEs and frontend test suites define current
renderer and generation coverage.

## Vertical 5: Ari infers and accepts an EDI relation

### User outcome

Ari helps a domain expert map an EDI transportation document such as an X12 204 load tender into a
canonical transportation model. Ari proposes high-quality mappings with evidence, exposes
ambiguity, accepts human corrections, and produces a portable Cohesive relation that can be
validated, executed, documented, and reused independently of Ari.

### Producer lifecycle

1. EDI and target-domain sources produce canonical shapes and semantic annotations.
2. Ari normalizes names, paths, ontology concepts, qualifiers, descriptions, and structural context.
3. Candidate generation proposes source expressions and relationship traversals for stable draft
   output slots.
4. Scorers attach features, model versions, scores, alternatives, and explanations.
5. Ari policy selects a candidate, records ambiguity, or abstains.
6. A reviewer accepts, changes, rejects, or supplies a mapping.
7. An Ari adapter lowers the proposal into a portable Cohesive Relation draft while keeping
   inference evidence in Ari.
8. Cohesive performs deterministic, shape-aware semantic acceptance.
9. The accepted Relation definition is persisted with provenance to the exact draft, shape graphs,
   and opaque Ari producer artifact.
10. Reference and target interpreters execute the accepted relation without depending on Ari.
11. Corrections and observed failures become versioned evaluation or training evidence in Ari.

### Ownership boundary

| Concern | Authority |
| --- | --- |
| Source and target shape semantics | Cohesive shapes and adapter-provided annotations |
| Draft slots, expressions, alternatives, and resolution state | Cohesive Relations draft |
| Feature extraction, models, scores, explanations, and review workflow | Ari |
| Type, presence, nullability, cardinality, and expression safety | Cohesive acceptance |
| Accepted executable relation | Cohesive Relations IR |
| Query realization and target capability | Cohesive compilers and adapters |
| Evaluation datasets, calibration, and model promotion | Ari |

Confidence never substitutes for semantic validation. A high-scoring unsafe mapping is rejected. A
low-confidence mapping may be semantically valid but remain unresolved under Ari review policy.

### Validation and verification

- Dataset splits are immutable, versioned, and checked for leakage.
- Candidate generation measures recall before scorer quality is evaluated.
- Evaluation reports top-k accuracy, calibration, abstention, ambiguity, semantic acceptance, and
  domain slices rather than one aggregate score.
- Golden examples cover qualifiers, repeated loops, optional segments, code sets, units, dates,
  nested structures, and relationship traversal.
- Cohesive acceptance tests reject unsafe conversions and incomplete required assignments.
- Accepted relations run against representative EDI observations and compare with curated canonical
  outputs.
- Corrections retain the proposal, model, reviewer, and accepted-definition lineage.

### Comprehension

A reviewer can see why a candidate was proposed, competing candidates, relevant source and target
semantics, validation findings, model and dataset versions, and the exact Cohesive draft node that
will change. A downstream developer can inspect the accepted relation without Ari-specific types.

### Current implementation anchor

Ari is a sibling product repository with relation inference, semantic shape modeling, training
workflows, EDI scenarios, evaluation datasets, and Cohesive package consumption. Cohesive Relations
already separates portable drafts from producer evidence. Complete product review and feedback
workflows remain Ari-owned work rather than new Cohesive semantics.

## Using the verticals during design

The implemented Transition, durable Process, Motion DQ, and relation-derived index-sync paths are
linked and runnable from the [Execution Kernel adoption guide](../EXECUTION_KERNEL_GUIDE.md). Those
examples deliberately reuse production-contract tests so the verticals do not acquire a second,
tutorial-only semantic model.

For any meaningful architectural change:

1. Identify the affected verticals.
2. State which language owns the new or changed meaning.
3. Update the canonical contract before duplicating behavior in a consumer.
4. Describe the reference interpretation and target capability requirements.
5. Add or revise conformance evidence.
6. Ensure generated artifacts and runtime observations preserve provenance.
7. Record current implementation gaps explicitly.

Verticals should evolve as the architecture is learned, but they should not be rewritten merely to
avoid exposing an implementation gap. Their value is that they keep the system oriented toward
coherent end-to-end semantics.
