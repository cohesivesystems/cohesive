---
kind: explanatory
status: accepted
authority: cohesive.adoption-case
owners: [cohesive-core]
applies_to: [cohesive]
last_verified: 2026-08-03
supersedes: []
---

# Why Use Cohesive Blocks?

This document is the economic and incremental-adoption case for Cohesive. The
[Cohesive vision](vision/cohesive-vision.md) defines the intended direction, the
[semantic model](concepts/semantic-model.md) defines the common conceptual contract, and the
[language family](architecture/language-family.md) assigns ownership across semantic domains. This
document asks the practical question: when does adding that semantic layer repay its cost?

Cohesive adds a semantic language, compilation, and interpretation layer. Teams must learn its
concepts, establish useful producers and review surfaces, understand diagnostics, and occasionally
extend an adapter. People should not normally hand-author canonical IR, but they must be able to
calibrate its intent and judge its evidence. That cost is real. The case for Cohesive is therefore
not that an extra abstraction is free, or that every application needs one. It is that a semantic
definition can earn back its cost through validation, verification, comprehension, change, and each
interpretation that would otherwise require another reconstruction of the same meaning.

The wrong comparison is usually a Cohesive relation against one hand-written SQL query, or a
Cohesive process against one application-service method. The relevant comparison includes the SQL,
object mapping, test fake, API contract, client types, workflow recovery, lineage, diagnostics,
documentation, and future backend migration that repeat or depend on the same semantics.

In shorthand:

> **Return = avoided semantic duplication + validation and verification leverage + comprehension +
> cheaper change + derived tooling + target optionality − modeling, calibration, and integration
> cost.**

The return starts small and compounds. A team should be able to adopt one block for one immediate
benefit, then connect other interpretations without replacing the original definition.

## The bitter lesson and the agentic test

The comparison is changing. A sufficiently capable coding agent can write application-specific code
directly, exploit infrastructure without waiting for a framework abstraction, and remove an external
dependency. It may eventually translate human specifications into systems—and continue evolving
those systems—more effectively than a fixed pipeline of intermediate representations and compiler
passes.

This weakens the traditional argument for libraries and frameworks. Reuse, encapsulation, and code
generation are not durable advantages when code itself becomes inexpensive to synthesize. Cohesive
must therefore be evaluated against agent-produced bespoke systems, not only against manually
written implementations.

Cohesive is justified only when its human-oriented languages provide leverage that direct synthesis
does not:

- people can validate whether the expressed system is what they intend;
- agents and tools can verify that a realization preserves the accepted meaning;
- people and agents can comprehend behavior, decisions, and consequences at useful abstractions;
- semantic change can drive impact analysis, migration, regeneration, rollout, and runtime
  evaluation across the full lifecycle; or
- durable requirements and capability evidence improve target use without hiding target strengths.

If direct agent synthesis provides these properties more reliably and economically for a particular
system, Cohesive should not be inserted. This is a continuing test, not an objection that can be
settled once.

Cohesive's current compilers and reference interpreters make the language claims executable and
falsifiable. They are concrete reference implementations, not the long-term moat. Future
realizations may use agent synthesis, learned compilation, search, direct execution, or a radically
different sequence of intermediate mechanisms. The durable value, if Cohesive has one, lies in the
human-oriented semantic language and the evidence connecting intent, change, implementation, and
operation.

This is an adoption decision guide, not a normative specification or release matrix. The package
READMEs describe the currently implemented surfaces. The
[golden verticals](use-cases/golden-verticals.md) show how the blocks are intended to compose, while
the [conformance strategy](quality/conformance.md) defines how interpreter and adapter claims should
be verified. Benefits that require an additional interpreter or adapter are option value until that
implementation exists; the
[Execution Kernel compatibility inventory](EXECUTION_KERNEL_COMPATIBILITY.md) calls out important
current gaps in Transitions and Processes explicitly.

## Benefit inventory

| Block | Low-cost adoption wedge | Direct return | Return when connected |
| --- | --- | --- | --- |
| **[Core shapes and expressions](../src/Cohesive/README.md)** | Describe shared shapes, identifiers, quantities, and portable expressions while continuing to use an ordinary C# application. | One inspectable model for types, field paths, nullability, expression requirements, and provenance; reusable validation and code generation inputs. | Relations, transitions, APIs, presentation, storage, and target compilers stop maintaining parallel descriptions of the same data and expressions. |
| **[Relations](../src/Cohesive.Relations/README.md)** | Begin as a typed query builder, DTO mapper, or deterministic in-memory relation/query. No heterogeneous storage is required. | Reusable logical relationships; exact field-demand analysis; in-memory tests; target query compilation; structured diagnostics; lineage and dependency information; a place to optimize access patterns without changing callers. | The same relation/query can read across registered sources, feed a process step, become an API operation, back a presentation data source, or inform indexes and materialized projections. Entity references declared by Transitions can compile into the same relationship catalog. |
| **[Transitions](../src/Cohesive.Transitions/README.md)** | Put the invariants and state changes of one behavior-rich entity behind a declared transition. | Preconditions, branching, sparse patches, outcomes, and effects become explicit and analyzable. The transition can be tested without an HTTP host or production store and interpreted through different persistence strategies. | Processes coordinate the same transitions; APIs expose them as commands; presentation actions bind to them; storage can attach optimistic concurrency and outbox behavior. Events, command-side state, and read-side needs can be derived from one policy instead of becoming independent models. |
| **[Processes](../src/Cohesive.Processes/README.md)** | Replace one coordination-heavy application service with an in-memory process definition before adopting a durable engine. | One logical workflow for reads, relation/query evaluation, transitions, requests, waits, signals, effects, retries, and checkpoints. The local runtime makes orchestration behavior testable without external infrastructure. | The same process can bind to a durable orchestration adapter, expose lifecycle operations through APIs, and coordinate Relations and Transitions without embedding their semantics in workflow-engine code. A custom or future runtime can implement the process contracts without changing the logical process. |
| **[API](../src/Cohesive.Api/README.md)** | Declare one operation and emit an OpenAPI description or TypeScript client while retaining ASP.NET for execution. | Shared operation identity, request/response shapes, routes, pagination, and scope policy; less server/client/schema drift; deterministic generated artifacts. | Queries, transitions, and processes become externally accessible without a second behavioral catalog. Presentation actions and data sources can bind to generated operation identities rather than hand-written strings. The same declarations can project to ASP.NET, OpenAPI, GraphQL, and TypeScript. |
| **[Presentation](../src/Cohesive.Presentation/README.md)** | Declare stable view, field, action, form, and data-source identities for one feature, then render it with an existing React integration. | Backend-owned UI semantics, generated frontend contracts, stable automation selectors, and reusable validation of navigation and interaction structure. | Data sources bind to Relations, actions bind to Transitions or Processes, and endpoint requirements bind to API declarations. A semantic change can propagate across backend and frontend artifacts with traceable provenance. |
| **Storage, Identity, Configuration, and Host** | Adopt only the provider-neutral contract that removes an immediate infrastructure dependency or duplicated configuration path. | Testable in-memory implementations, typed configuration projection, explicit source capabilities and limits, fail-closed identity lookup, and consistent host bindings. | These blocks supply infrastructure interpretations without introducing competing query, authorization, or domain models. Compiler policy can select targets while retaining the source semantic definition. |
| **AI** | Use a provider-neutral inference, vector, ontology, text, or training contract around one model-dependent capability. | Stable application semantics across model runtimes and training infrastructure; reusable ontology closure and text/vector utilities. | AI capabilities can share Cohesive shapes, storage, processes, APIs, and provenance rather than living behind an isolated provider-shaped subsystem. |

### Relations: where target independence pays earliest

Relations can justify themselves in a PostgreSQL-only system as a query builder, compiled mapper,
in-memory test interpreter, or source of field-demand and index/materialization evidence. The case is
much stronger when facts span PostgreSQL, Cosmos DB, search indexes, APIs, caches, or supplied
objects. The logical relationship remains fixed while the compiler decides whether a traversal is a
native join, batched lookup, bounded acquisition, or local correlation. Physical placement and
access-pattern optimization do not leak back into every consumer.

This is also an option value: a team need not predict its final storage topology when it declares a
relationship. The declaration starts paying immediately through authoring and testing, then becomes
more valuable if topology or access patterns change.

### Transitions: one policy for state change

Transitions cost more than an ordinary method because state requirements, invariants, patches,
outcomes, and effects must be made explicit. In return, the system gains one inspectable policy that
can support command execution, event/outbox emission, sparse state acquisition, deterministic tests,
API commands, and process steps. CQRS and event-sourced realizations become interpretations of the
same behavior rather than reasons to reimplement that behavior in handlers, aggregates, projectors,
and controllers.

The payoff is highest for behavior-rich entities, cross-cutting invariants, multiple persistence
strategies, or systems where auditability matters. A simple CRUD record with one database and no
meaningful invariants may not recover the authoring cost.

#### A day in the life of an entity transition

Consider a transition such as `Load.AssignCarrier(loadId, carrierId)`. In isolation, using a
dedicated C# authoring language can look excessive compared with writing a method, even with the
metaprogramming power provided by C# expressions. The comparison changes when the transition's full
lifecycle is considered.

Application behavior must be reflected across many otherwise disconnected caverns: domain code,
storage, APIs, frontend state, orchestration, authorization, testing, operations, and documentation.
Conventional applications often reproduce those reflections by hand. The copies drift because the
original method is executable but its meaning is not available as a first-class value.

A Cohesive transition is instead a semantic object observed by validators, planners, storage
adapters, APIs, user interfaces, process runtimes, test systems, operational tooling, and development
agents. Authoritative execution is only one interpretation in its lifecycle:

1. **Production and lowering.** An agent, developer, inference system, or other producer expresses
   inputs, preconditions, branches, state changes, outcomes, and effects through a C# surface or
   another language frontend. The surface lowers to canonical, portable Transition IR; the producer,
   CLR expression, prompt, or delegate does not become the durable semantic authority. People can
   inspect the IR or a faithful projection without being expected to author its normalized form.
2. **Identity and provenance.** The transition receives stable definition, revision, node, input,
   outcome, and interaction identities, together with its origin and fingerprint. Other blocks can
   refer to the behavior without pointing to a CLR method or copying its name.
3. **Static analysis.** Compiler interpretations determine which state fields may or must be read,
   which fields may be written, which branches and outcomes are possible, which effects may occur,
   whether expressions are well-typed, and which ambient or target capabilities are required.
4. **Testing, simulation, and verification.** The same definition supports example tests,
   generated boundary cases, in-memory interpretation, property-based tests, invariant checking,
   state-machine exploration, and differential tests between interpreters. Tests exercise the
   semantic authority rather than a test-specific reconstruction of it.
5. **Presentation binding.** A presentation interpreter can bind a button, form, or flow to the
   transition's stable identity. Inputs, confirmation behavior, outcome handling, conservative
   enablement, and affected data sources can be connected without inventing another action model.
6. **API binding.** An API interpreter projects the transition's inputs, outcomes, authorization
   requirements, and diagnostics into an operation. ASP.NET endpoints, OpenAPI descriptions,
   GraphQL schemas, and other transports remain interpretations of the same behavior.
7. **Frontend–backend boundary crossing.** Generated identifiers, types, and clients let the
   frontend invoke the transition without duplicating routes, input records, outcome cases, or
   semantic strings. A suitable subset may also be interpreted for optimistic evaluation, offline
   validation, and reconciliation while the backend remains authoritative.
8. **Authorization and security analysis.** Identity and policy interpretations determine who may
   request the transition and under which scope. Static analysis can identify over-broad reads,
   unintended writes, unsafe effects, or information exposed through inputs and outcomes.
9. **Storage binding and access planning.** Exact read requirements let a planner acquire only the
   state the transition needs rather than loading an entire entity. The selected storage adapter must
   supply the required consistency, sparse-read, atomic-write, and outbox capabilities. Access
   patterns, partition choices, indexes, and materialized projections can be evaluated from the same
   evidence.
10. **Invocation planning.** The compiler combines semantic requirements with current
    configuration, convention-derived policy, and adapter capabilities. It selects an attributable
    realization—or emits a precise diagnostic when the target cannot preserve the requested
    guarantees.
11. **Concurrency and consistency.** Transition requirements inform optimistic concurrency,
    isolation, conflict behavior, atomicity, idempotency, and retry policy. These guarantees become
    explicit compiler and adapter obligations instead of incidental repository behavior.
12. **Execution and decision.** An interpreter evaluates the preconditions and structured body
    against the supplied input and acquired observation. It produces an explicit decision such as
    applied or rejected, including attributable evidence about the path taken.
13. **Commit and integration effects.** The resulting sparse patch, new version, events, outbox
    messages, requests, and continuations are committed according to the declared guarantees. CQRS,
    event-sourced, snapshot-oriented, and other persistence strategies remain realizations of one
    transition policy rather than independent implementations of it.
14. **Participation in processes.** A process invokes the transition by semantic identity while
    coordinating other reads, relations, entity transitions, waits, retries, and effects. The
    transition can participate in a multi-step or multi-entity transaction, saga, or process manager
    without embedding its rules in the orchestration engine.
15. **Propagation to consumers.** Committed outcomes can invalidate presentation data, update read
    models, continue processes, and notify external consumers. Each derived effect retains a path
    back to the transition and node that produced it.
16. **Observability, audit, and operational control.** Traces and audit records can explain which
    revision executed, why a request was rejected, which fields were consulted, what changed, which
    effects were emitted, and which adapter guarantees supported the commit. Stable transition
    identity also supports metrics, deployment gates, feature policy, and runtime controls.
17. **Documentation and discovery.** Tooling can generate state-change catalogs, diagrams, examples,
    endpoint documentation, and operator-facing explanations from the executable semantic authority.
18. **Evolution and migration.** Revision comparison can identify affected views, endpoints,
    processes, events, storage requirements, indexes, clients, tests, persisted entities, and
    in-flight executions before a change is deployed.
19. **Agentic development.** An agent can work from explicit inputs, branches, reads, writes,
    invariants, effects, capabilities, consumers, and provenance. It reasons about a bounded semantic
    object and verifies its projections instead of reconstructing intent from scattered code.

The first-class IR is what makes these observers independent. They do not need to instrument one
another, scrape generated artifacts, or maintain synchronized descriptions of the transition. Each
interpreter receives the same semantic object and produces the realization appropriate to its
cavern.

The relevant comparison is therefore not **a transition DSL versus a C# method**. It is **a
transition IR plus its interpreters versus a C# method and all of its manually synchronized
echoes**. A field-read set alone can inform sparse acquisition, authorization review, dependency
analysis, test generation, access-pattern planning, and agent context. A state patch can inform
persistence, events, UI invalidation, audit, and replay. A precondition can inform runtime
enforcement, action enablement, API diagnostics, documentation, and model checking. The authoring
cost pays back through reuse of the same semantic evidence.

### Processes: application behavior without an orchestration-engine identity

A process captures coordination semantics above any one execution substrate. It can replace the
application-service layer that otherwise mixes reads, commands, retries, effects, time, and vendor
SDK calls. Local execution provides a cheap starting point and fast tests; durable execution can be
introduced where recovery requirements justify it. The native durable runtime is implemented over
the Process store contract. Durable Task is an accepted parallel interpreter target whose executable
adapter is not yet implemented. Temporal and other custom managers are later target-adapter
opportunities, not assumptions embedded in the process definition.

The intended semantic surface includes durable execution, process managers, sagas, compensation,
and other workflow patterns. Not all execution-kernel guarantees are complete today. Before relying
on advanced behavior such as durable signal arbitration, parallel joins, or compensation planning,
consult the [Execution Kernel compatibility inventory](EXECUTION_KERNEL_COMPATIBILITY.md).

## Why the blocks are worth more together

The largest externality is that a fact declared in one block becomes evidence for another:

- A shape supplies the contracts used by relations, transitions, APIs, generated clients, and views.
- An entity reference can supply a canonical relationship instead of a separately maintained join.
- A relation/query can supply a process read, API query, presentation data source, lineage report,
  test interpreter, and backend plan.
- A transition can supply a process step, API command, presentation action, effect contract, and
  storage requirement.
- A process can supply API lifecycle operations, operational status, test scenarios, and UI flows.
- Capability evidence and provenance can explain how every derived artifact was selected and which
  semantic node produced it.

This compounding is the central economic claim. Cohesive should not require adopting the whole suite
to get value, but every additional interpretation should reuse existing semantic authority rather
than create another model that happens to resemble it.

## The agentic-development dividend

Agentic development is a primary design target, not an incidental benefit. Persisted IR changes what
an agent has to infer. Instead of reconstructing intent from controllers, SQL, serializers, workflow
code, and frontend strings, an agent can produce and inspect explicit shapes, relationships,
invariants, field requirements, capabilities, stable identities, revisions, changes, and provenance.
That enables narrower and more verifiable work:

- locate every consumer of a semantic field or operation;
- predict which targets and generated artifacts a change affects;
- validate a proposed change against invariants and target capabilities;
- generate migrations, tests, documentation, and client updates from the same change;
- compare interpreters or execution plans for semantic equivalence;
- explain why a compiler selected a realization or rejected one.

This does not make agents automatically correct. It replaces ambiguous code archaeology with a
smaller, structured evidence set and gives both agents and humans stronger ways to check the result.
Agents remain producers, interpreters, reviewers, or operators around the semantic authority; their
prompts, plans, and confidence do not replace canonical IR or deterministic acceptance boundaries.
The IR must remain readable enough for people to orient themselves and calibrate agent output, but
human hand-authoring is not the expected throughput path. Human-oriented review projections,
semantic diffs, examples, and explanations may be more important interfaces than a general-purpose
IR editor.

## An incremental adoption path

1. **Take a local win.** Have an agent, host-language frontend, or other producer use one block for a
   concrete pain: a relation as a mapper/query, a transition for one domain invariant, a process for
   one brittle workflow, or API declarations for client generation.
2. **Add a cheap interpretation.** Run it in memory, generate a contract, emit documentation, or add
   static validation before changing production infrastructure.
3. **Connect adjacent blocks.** Bind the relation to a process, the transition to an API action, or
   the API to presentation. This is where duplication begins to disappear.
4. **Move physical policy behind adapters.** Introduce heterogeneous sources, durable execution, or a
   new API/frontend target only when the operational need appears.
5. **Persist and govern the IR.** Version definitions, retain provenance, run compatibility checks,
   and use the model as durable input to agents and operational tooling.

Each step should provide independent value. If it does not, stop at the previous step; suite-wide
adoption is not itself a success criterion.

## When Cohesive is—and is not—a good trade

Cohesive tends to pay back quickly when semantics cross infrastructure boundaries, the same rules
are duplicated across application layers, workflows require replay or recovery, multiple consumers
need consistent contracts, or the system is expected to evolve for years.

It is a weaker trade for short-lived applications, straightforward CRUD over one stable database,
teams whose dominant work is intrinsically backend-specific, cases where only one implementation of
a rule will ever exist, or systems where an agent can directly synthesize, verify, explain, and
evolve the implementation with less semantic machinery. Direct access remains a legitimate escape
hatch for such work. It should be explicit and attributable so it does not silently become a second
semantic authority.

The practical standard is simple: do not justify a block by the abstraction it introduces. Justify
it by the independent implementations, tests, integrations, and future changes that its semantic
model makes unnecessary or materially safer. Then require the resulting interpretation to publish
the capability, provenance, and conformance evidence that makes that return credible.
