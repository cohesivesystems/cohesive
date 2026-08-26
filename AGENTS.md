# Cohesive Agent Notes

This repository is in an early R&D phase.
Optimize for learning, iteration speed, and sharp architectural progress. 
Move fast and break things when that helps validate the right semantic model, but keep the code understandable enough that good ideas can be retained and generalized.
Maintain a single source of truth for data, types, and behaviors.
Favor a declarative approach to building reusable components.
Frontend views and capabilities should be declared on the backend and projected onto the frontend.
The foundational paradigm is to isolate the semantics and attach various interpretations to infrastructure.

## What We Are Building

## Cohesive Vision

Cohesive is a comprehensive family of high-level languages, together with a corresponding family of compilers, for expressing software systems end to end across application layers: entities, relations, processes, interfaces, infrastructure, and operational behavior.

Its purpose is to let developers define the semantics of a system before committing those semantics to particular technologies. The language family captures the problem domain and the system’s intended behavior as first-class constructs. Compilers then interpret and project those constructs into concrete infrastructure such as storage engines, messaging systems, caches, distributed runtimes, AI training systems, tensor operations, and mathematical computation frameworks.

Concepts that are normally implicit, fragmented, or embedded in implementation details are made explicit in the languages themselves. This allows related concepts to be composed directly, analyzed as a whole, and transformed across layers without losing their meaning.

The result is not a single language or a replacement technology stack, but a coherent language-and-compiler architecture: a semantic layer for describing what a system is, paired with compilers that determine how that meaning is realized in existing infrastructure.

## Architectural Stance

An underlying principle of the system is language-oriented programming. 
We define semantic constructs in the form of an IR and provide interpretations for them. 
The semantic surface should lead; infrastructure concerns should attach to that surface instead of dictating it.

Cohesive should not collapse to the lowest common denominator of the infrastructure it integrates with.
Instead, it should model the capability closure of supported targets and expose a first-class capability model so higher-level semantics can use what the underlying systems can actually do.

### Code Quality and Optimization Protocol

Optimal code is contextual. Do not optimize for a single metric, minimum line count, maximum reuse, or speculative generality. Follow the normative [Cohesive Code Quality and Optimization Model](docs/quality/code-quality.md).

By default, prefer the implementation that:

1. Preserves intended semantics and makes invalid or unsupported states explicit.
2. Maintains one identifiable source of truth for each concept.
3. Introduces the fewest independent concepts needed to express the design.
4. Makes ownership, guarantees, failure behavior, and target differences inspectable.
5. Is easy to verify, diagnose, and change.
6. Meets demonstrated performance and operational requirements.
7. Avoids complexity justified only by hypothetical future requirements.

"Small" means a small conceptual surface, not merely fewer lines or types. Syntactic duplication may be acceptable; duplication of semantic authority is not.

For a nontrivial change, identify the semantic authority, invariants, explicit constraints, likely change axes, and relevant sibling implementations before choosing a design. If viable designs differ materially in semantics, performance, conceptual complexity, extensibility, or reversibility, state the tradeoff and the priority being applied. Ask for direction when that priority depends on unavailable product or architectural intent.

Before completing a nontrivial change, audit semantic preservation, authority, abstractions, types, performance, verification, and explainability. Report material tradeoffs and intentionally deferred improvements. Metrics such as cyclomatic complexity, coverage, allocations, dependency count, or line count are evidence that may prompt investigation; they are not standalone definitions of quality.

### Agentic Authoring and Evolution Protocol

Cohesive is designed for agent-first production and evolution, human-centered expression and review, and human-governed acceptance. Agents and tools should produce semantic models through authoritative, inspectable interfaces and human-legible authoring projections rather than make opaque generated output authoritative.

When adding or changing a semantic construct, determine how an agent can inspect, author or patch, validate, explain, compare, and reconcile it with source intent. Preserve stable identity, exact revisions, deterministic serialization and fingerprints, provenance, source maps, structured diagnostics, and bounded context retrieval where the owning layer can support them. A construct available only through handwritten host-language code is incomplete unless that limitation is explicit and temporary.

In C#, prefer typed expression-based fluent builders as the human-reviewable authoring projection when they faithfully cover the semantics. Builders are producers, not semantic authorities. Given fixed authoring input, referenced contracts, producer/compiler version, convention profile, and explicit configuration, lowering must be deterministic. No callback, closure, ambient service, reflection behavior, or arbitrary host-language executable dependency may survive into canonical IR. Validation, compilation, and interpretation consume the materialized IR, and representative fluent/direct-IR equivalence should be tested where practical.

Preserve evolution latitude while the complete Change IR is still being distilled. Prefer immutable, comparable revisions and explicit domain-owned operations over destructive in-place mutation or a speculative universal patch framework. Do not conflate intent, semantic change, realization, deployment, and runtime observation. Treat OpenSpec and other specification formats as attributable producers of intent, drafts, and proposed changes through adapters; they do not become parallel executable authorities.

### Portable Semantic IR and Multiple Interpretations

Cohesive semantic IRs are portable, durable system models rather than temporary compiler data structures. An IR may be authored through a host-language DSL, inferred by an engine such as ARI, imported from another representation, or produced by tooling. These are producers of the IR, not independent sources of semantic truth. Once materialized, the IR should be explicitly persisted, versioned, managed, and available for inspection.

Portability has several dimensions:

- **Authoring portability:** semantics may be authored, inferred, imported, or generated.
- **Host-language portability:** the same model may be projected into C#, TypeScript, Java, Python, Rust, or another language.
- **Target portability:** the model may be lowered to different storage, runtime, UI, API, AI, or infrastructure backends.
- **Placement portability:** behavior may execute on the backend, frontend, device, edge, database, accelerator, or orchestration runtime.
- **Purpose portability:** the IR may support execution, validation, optimization, simulation, testing, visualization, documentation, migration, and monitoring.
- **Temporal portability:** persisted IR versions support compatibility analysis, replay, migration, and the evolution of long-lived state and processes.

An interpretation does not need to execute the IR. Compilers, optimizers, validators, simulators, visualizers, documentation generators, migration planners, test generators, and monitoring projections are all interpretations of the same semantic model.

For example:

- A relation IR inferred by ARI may compile into SQL, document queries, search queries, in-memory execution, materialized projections, lineage reports, or index diagnostics.
- A transition IR may execute authoritatively on the backend, run optimistically in TypeScript on the frontend, support offline reconciliation, compile into a database transaction or event-sourced handler, or drive simulation and property-based tests.
- A process IR may execute through different orchestration runtimes while also supporting deterministic replay, visualization, compensation analysis, operational monitoring, and test runtimes.
- Presentation, API, and identity IRs may generate multiple client and server implementations, accessibility and authorization checks, contract tests, documentation, and deployment policy.
- AI and numerical IRs may compile to PyTorch, ILGPU, CPU execution, ONNX, or distributed runtimes while also supporting shape analysis, differentiation, optimization, evaluation, and visualization.
- Entity and infrastructure IRs may produce schemas, migrations, serializers, deployment resources, local emulators, cost estimates, security checks, and drift diagnostics.

The canonical IR is the source of truth. Generated code, execution plans, schemas, deployment resources, and other target artifacts are derived interpretations and should retain provenance to the IR nodes and compiler decisions that produced them. Backend-specific behavior must be represented through explicit IR extensions, compiler configuration, or attributable overrides rather than invisible changes to generated artifacts.

Each interpreter should declare the IR versions, capabilities, constraints, and semantic guarantees it supports. Different interpretations may have different performance characteristics and physical realizations, but they must preserve the declared semantics or emit precise diagnostics. Reference interpreters, adapter conformance suites, and differential tests should be used where practical to verify equivalence across interpretations.

### Capability-Driven Compilation

Cohesive targets infrastructure through ports and adapters, but adapters are not uniform facades that erase backend differences. Each adapter describes the capabilities, constraints, guarantees, and limits of its target. Semantic constructs declare requirements independently of those targets, and compilers match requirements to capabilities when producing a realization.

A realization may be native, composed from multiple facilities, constrained to a declared operating boundary, supplied by an explicit override, or unavailable. Composed strategies must preserve the requested semantics. The compiler must not silently weaken guarantees such as atomicity, ordering, durability, consistency, isolation, or recovery.

Compiler configuration acts as policy. It may select preferred lowering strategies, permit declared tradeoffs, introduce auxiliary infrastructure, configure diagnostic severity, and register target-specific extensions. Generated artifacts and compiler decisions should retain provenance back to the semantic requirements and capability evidence that produced them.

Capability mismatches produce precise, structured diagnostics with actionable resolution paths. Diagnostics are part of the product surface and should be usable by validation tools, tests, deployment gates, monitoring, and operational tooling, not only displayed as compiler messages.

Overrides and direct backend access are intentional escape hatches. They must remain explicit, local, inspectable, and attributable rather than becoming hidden side channels or duplicated semantic models.

Think of Cohesive as a compiler or operating-system hardware abstraction layer rather than a generic abstraction facade: the semantic IR remains stable while target descriptions, lowering strategies, intrinsics, optimizations, diagnostics, and adapters determine how that meaning is realized.

### Convention-Driven Defaults

Cohesive should support convention over configuration. Common cases should require only the semantic declaration; compilers, adapters, and runtime integrations should supply sensible defaults for target selection, naming, registration, lowering strategies, generated artifacts, and operational behavior.

Conventions are default compiler policy, not hidden behavior. Every convention-derived decision should be deterministic, inspectable, and attributable to the convention or profile that supplied it. Tooling should be able to explain the effective configuration and distinguish explicit declarations from inferred defaults.

Configuration should be incrementally refinable. Developers may begin with framework defaults and introduce scoped profiles, target-specific options, compiler extensions, or local overrides only where the default realization is insufficient. Configuration precedence, from highest to lowest, is:

1. Explicit local declarations and overrides.
2. Scoped application or subsystem profiles.
3. Adapter and compiler conventions.
4. Framework-wide defaults.

Conventions may select among semantically equivalent realizations, but they must not invent semantic requirements or silently weaken guarantees. When no safe default exists, the compiler should require an explicit decision or emit a diagnostic rather than guessing.

Conventions should be composable, replaceable, and independently testable. Avoid ambient state, implicit service location, order-dependent registration, and defaults whose behavior cannot be reproduced from the semantic model, selected targets, compiler configuration, and convention set.

When designing abstractions:

- Start from meaning, not transport, or vendor APIs.
- Prefer explicit semantic models over ad hoc glue code.
- Keep infrastructure-specific behavior in interpreters/adapters.
- Keep the capability model as the single source of truth for compiler planning, validation, documentation, and observability.
- Preserve capabilities and guarantees when projecting semantics onto concrete systems.
- Provide deterministic conventions for common cases, with explicit configuration and narrowly scoped overrides available incrementally.
- Make effective configuration explainable, including which values were declared, inherited, inferred by convention, or supplied by an adapter.
- Treat authoring DSLs, inference engines, and importers as producers of canonical, persisted IR rather than independent semantic authorities.
- Design semantic IRs for execution and non-execution interpretations, including validation, simulation, testing, documentation, migration, and observability.
- Require derived artifacts and runtime observations to retain provenance to their source IR and interpretation decisions.
- Validate semantic equivalence across interpreters with reference implementations, conformance suites, or differential tests where practical.
- When backend IR is the source of truth for identifiers, shapes, routes, actions, selectors, roles, workflows, states, permissions, or other semantic data, favor generated codegen artifacts over hand-written strings or duplicated frontend models. If a generated artifact is missing, extend the codegen path rather than introducing a parallel constant unless the duplication is explicitly temporary and documented.

### Abstraction Distillation and Type Discipline

Cohesive has many semantic layers, compilation phases, and target interpreters, but a layer, phase, or target boundary does not by itself justify another type. Continuously distill recurring concepts and mechanisms into the smallest coherent set of abstractions that preserves semantic distinctions, invalid-state prevention, provenance, and target capabilities. Prefer semantic compression over type proliferation.

When adding or changing a type, helper, compiler, or adapter:

- Search the repository, especially sibling target implementations and the core/prelude, for the same concept or algorithm before introducing a parallel implementation.
- In an official adapter, treat its shared construction, compilation, naming, escaping, parameterization, execution, capability, and diagnostic layers as the default infrastructure authority rather than optional helpers. Before emitting target commands or artifacts directly, reuse or minimally extend the existing adapter mechanism. A feature-local emitter is appropriate only when target semantics materially differ or the shared gap is explicitly temporary; document the divergence and track consolidation.
- Identify the semantic authority for the concept. If multiple enums, records, constants, or switch tables describe the same closed set and are expected to evolve together, default to one canonical type or catalog and project from it.
- Treat exhaustive one-to-one conversions between types with identical cases as evidence of a duplicated model. Keep separate types only when they enforce a real distinction such as different valid states, units, ownership, lifecycle, versioning, serialization contracts, or capability guarantees.
- Do not duplicate a type merely because it might diverge later. Share the present semantic model and split it when a concrete differing invariant appears. When an external or persisted boundary requires a distinct type, prefer a thin attributed wrapper or explicit projection over a second independently maintained case list.
- Centralize mappings from semantic scalar kinds to target types, encodings, constants, readers, writers, and capability evidence. Do not scatter parallel switch expressions across authoring, validation, compilation, execution, and serialization.
- Prefer extending a cohesive existing abstraction or using a small function, value object, catalog, or policy over creating a family of phase-specific records and interfaces. Avoid inheritance hierarchies and generic frameworks whose only purpose is removing superficial syntactic repetition.
- Consider the net conceptual and type count of a change. New types should make an invalid state unrepresentable, establish an ownership or versioning boundary, or remove more duplication and ambiguity than they add.

Use recurrence as a prompt for architectural review:

- At the second substantial implementation of a mechanism, compare the implementations and identify the stable common shape.
- At the third implementation, shared extraction is the default expectation unless target semantics materially differ. If extraction is deferred, keep the implementations visibly parallel and document the concrete point of divergence so a later consolidation remains straightforward.
- Look for repetition beyond identical syntax or type names. Common abstractions often hide in control flow, data flow, traversal, normalization, validation, canonicalization, naming, diagnostics, serialization, builders, caching, allocation strategy, and lifecycle management.
- Extract target-independent mechanism while parameterizing target policy. Across families of compilers and interpreters, orchestration, input-validation flow, traversal, status derivation, diagnostic normalization, provenance checks, deterministic ordering, and result invariants are likely shared mechanisms; capability matching, binding evidence, lowering choices, target construction, and target serialization remain interpretation policy.
- Extract the smallest complete operation with a stable contract rather than a collection of incidental utility methods. Place it in the lowest layer that owns the shared meaning without making core packages depend on adapters or infrastructure.
- Reuse existing primitives and builders before introducing local equivalents. If the existing primitive is close but incomplete, prefer generalizing it coherently over creating another narrowly named helper.
- Keep uncertain shared abstractions internal while their boundaries are being learned. Promote them to public framework concepts only when their semantics, ownership, and extension model are clear across multiple uses.
- Prefer conformance tests and shared test fixtures for common compiler contracts over copying the same behavioral tests for every target. Keep target-specific tests for genuine capability and lowering differences.

Before completing a change that adds types or repeats a mechanism, perform a brief abstraction audit: inspect related implementations, identify duplicated models and behavior, consolidate what now has a stable shared meaning, and note any intentionally separate concepts whose invariants justify the distinction. Do not create an abstraction solely to satisfy this audit; the goal is fewer independent concepts, not more indirection.

## Repository Shape

The reusable toolchain is developed primarily through the `Cohesive.*` libraries:

- `src/Cohesive`: core shape model, primitives and prelude.
- `src/Cohesive.Configuration`: configuration building blocks (configuration profiles, rich dependency selection, etc.).
- `src/Cohesive.Relations`: relationship, projection, and query/aggregation semantics.
- `src/Cohesive.Transitions`: entity transitions and invariants.
- `src/Cohesive.Processes`: multistep workflows that can involve entity transitions, queries, waits, or arbitrary effects.
- `src/Cohesive.Presentation`: a UI/presentation layer language that is compiled/projected onto concrete UI rendering systems like React/Blazor/Angular, etc.
- `src/Cohesive.AI`: semantic AI, inference, training, vectors, numerics, and text-oriented components.
- `src/Cohesive.Simulation`: provider-neutral deterministic generation and simulation semantics over core shaped observations.
- `src/Cohesive.Api`: API declaration language that can generate OpenAPI/GraphQL/gRPC/etc.
- `src/Cohesive.Api.Execution`: optional execution-control API composition over generic API and canonical execution contracts.
- `src/Cohesive.Storage`: generic storage abstractions.
- `src/frontend/*`: TypeScript incarnations of Cohesive.Presentation as well as IR projection and rendering modules for React, component libraries, styling libraries, etc.
- `src/Cohesive.Adapters.*`: adapters to external infrastructure.

Adapters to external infrastructure belong in the `Cohesive.Adapters.*` projects under `src/adapters`. 
That is where integration with external storage, runtimes, ML systems, transport layers, and other concrete platforms should live.

## Documentation Expectation

- Reusable components should be well-documented. 
- Public APIs, semantic models, adapter boundaries, and performance-sensitive behavior should be explained clearly enough that other contributors can extend the system without reverse-engineering intent from implementation details alone.
- Document public and protected APIs using XML documentation, including record constructor parameters, method and constructor parameters, return values, enum cases, and expected exceptions.
- Add a `<param>` element for every method, constructor, delegate, and record constructor parameter. Documentation may repeat information conveyed by the parameter name or type when that repetition improves readability. Include additional semantics such as units, valid ranges, null behavior, ownership, lifetime, defaults, and interpretation where relevant.
- Add a `<returns>` element for every non-`void` method. Describe what the returned value represents and any important nullability, ownership, mutability, caching, laziness, or lifetime behavior. For `Task`, `ValueTask`, and other wrappers, document the resolved result rather than merely restating the wrapper type.
- Add an `<exception>` element for exceptions that form part of a method's or constructor's failure contract. Include directly thrown exceptions and predictable propagated exceptions callers may need to handle. State the precise condition that causes each exception.
- Do not omit documentation solely because a member, parameter, or return value appears self-explanatory. Brief documentation is acceptable when the contract is simple; prefer clarity and consistency over avoiding repetition.
- Keep parameter, return-value, and exception documentation synchronized with the implementation as contracts evolve. Do not enumerate incidental failures or universal runtime exceptions that are not meaningful parts of the API contract.


## Coding Style
- Use modern C# features (collection literals/expressions, immutable collections, records, nullables, extension members/methods, switch expressions, lambda syntax, etc.).
- Favor collection expressions over explicit collection constructions (e.g., `[..items]` vs `items.ToArray()`)
- Favor read-only collection abstractions (minimal required interfaces).
- Favor named arguments at call sites when they make code understandable in a plain-text review without IDE parameter hints. Be especially generous for literals and primitive values, adjacent arguments of the same type, non-obvious units or policy choices, and constructors or factories with several scalar arguments.
- Positional arguments remain appropriate when the method name and argument expressions make the meaning immediately clear, particularly for short conventional calls. Do not add names that merely repeat self-evident expressions or make a compact call materially harder to scan.
- Treat recurring dependence on named arguments as possible API-design evidence: where callers repeatedly need names to distinguish several loosely related scalar values, consider a semantic value object, options type, or more intention-revealing operation instead of relying on argument labels alone.
- Avoid magic strings, magic numbers. Project everything from a single source of truth.
- Type local factory/sample values against generated contracts or narrow explicit interfaces instead of relying on anonymous inferred object shapes.
- Avoid external dependencies unless discussed and approved.
- Reflection metadata access should be centralized and/or cached.

Performance techniques for hot paths:
- Minimize allocations
- Pooling (objects, buffers)
- in, ref, out modifiers
- Batching
- ValueTask
- Struct-of-arrays (SoA, AoS)
- Reduce object lifetime
- Favor contiguous memory (Span, Memroy<T>, ArrayPool<T>)
- Use Span<T>, ReadOnlySpan<T> for strings and so on.
- Use stackalloc for small temporary buffers.
- Avoid boxing when possible.
- Optimize field ordering (group similar-sized fields together)
- Pre-size collections.
- Use SIMD or intrinsics where data parallelism exists.


## C# Performance Guidelines

Don't optimize prematurely but avoid design decisions that are likely to be costly to change later.
Prefer clear, intent-revealing implementations on paths not demonstrated to be hot, while preserving
optimization latitude: keep operation boundaries, data ownership, evaluation behavior, and side
effects explicit enough to substitute a more efficient realization without changing semantics or
unrelated callers. LINQ is appropriate when it best expresses intent on a non-hot path; if evidence
later identifies the path as hot, keep the operation coherent enough to replace it with a fused loop,
span-based implementation, batching, or another measured strategy. Do not build speculative
abstractions merely to make hypothetical optimization possible.

### Memory
- Minimize allocations and object lifetimes.
- Use object and buffer pooling where appropriate.
- Prefer contiguous memory: arrays, `Span<T>`, `Memory<T>`.
- Use `stackalloc` for small temporary buffers.
- Avoid boxing and hidden allocations.
- Pre-size collections.
- Do not materialize an intermediate array, list, or immutable collection only to immediately filter, project, sort, or copy it into another collection. Fuse the work into a single loop, span operation, or pre-sized final builder.
- When an exact-capacity `ImmutableArray<T>.Builder` is full and ownership transfers to the result, prefer `MoveToImmutable()` over `ToImmutable()`. Retain `ToImmutable()` when the builder may be reused or its count does not equal its capacity.
- Preserve defensive copies at caller-owned mutable boundaries, but provide explicit trusted-ownership paths for internally produced immutable storage so it is not copied again.
- When a normalizing boundary receives already-canonical immutable input, retain it after validating canonical order instead of unconditionally sorting and rematerializing it.
- Audit `params` calls and collection expressions on hot paths: they can allocate a temporary array that is immediately copied by the callee. Add fixed-arity, span, or immutable overloads where this pattern recurs.

### Data Layout
- Prefer cache-friendly layouts.
- Use struct-of-arrays for large homogeneous workloads.
- Keep structs small and immutable.
- Order fields to reduce padding.
- Prefer blittable data for interop and SIMD-heavy paths.

### Hot Paths
- Avoid LINQ, reflection, and unnecessary delegates in hot loops.
- Avoid materialize-then-project pipelines such as `ToArray().Select(...)`; project directly into the final destination in one pass.
- Reduce virtual and interface dispatch.
- Keep frequently called methods small and inlineable.
- Use `in`, `ref`, and `out` only when measurement justifies them.
- Optimize the common branch.

### Concurrency
- Batch work to amortize synchronization and I/O overhead.
- Minimize lock contention and shared mutable state.
- Partition work by ownership where possible.
- Use `Channel<T>` for producer-consumer pipelines.
- Watch for false sharing.

### Async
- Avoid unnecessary async state machines.
- Use `ValueTask` only on measured, frequently synchronous paths.
- Reuse completed tasks where appropriate.
- Avoid sync-over-async and thread-pool blocking.

### CPU
- Use SIMD or hardware intrinsics for suitable numerical workloads.
- Prefer source-generated serializers, regexes, and metadata.
- Avoid redundant encoding, parsing, and copying.
- Use efficient collection types for the access pattern.

### Validation
- Profile before optimizing.
- Benchmark representative workloads with BenchmarkDotNet.
- Track allocations, GC, CPU, contention, and tail latency.
- Require before-and-after measurements for nontrivial optimizations.


## Framework Design Guidelines

Note that this framework is still in early stages, so API stability and backwards compatibility are not mandatory. 

### API Design
- Keep the public API small, explicit, and difficult to misuse.
- Prefer stable abstractions over implementation-specific types.
- Use consistent naming, parameter ordering, and lifecycle conventions.
- Avoid boolean parameters; use enums, options, or distinct methods.

### Extensibility
- Design extension points intentionally; do not expose internals by accident.
- Prefer composition, interfaces, delegates, and policies over inheritance.
- Keep core abstractions independent of specific frameworks and infrastructure.
- Provide sensible defaults with narrowly scoped override points.
- Avoid service-locator and ambient-context dependencies.

### Correctness
- Make invalid states unrepresentable where practical.
- Validate configuration and inputs at system boundaries.
- Define ownership, mutability, thread-safety, and lifetime semantics explicitly.
- Specify ordering, retry, cancellation, idempotency, and failure behavior.
- Fail early with actionable error messages.

### Performance
- Keep common paths efficient without requiring specialized usage.
- Avoid hidden allocations, reflection, blocking, and unbounded buffering.
- Expose batching, streaming, pooling, and cancellation where relevant.
- Do not return pooled or mutable internal state without clear ownership rules.
- Measure abstraction overhead and preserve escape hatches for hot paths.

### Dependency Management
- Minimize required dependencies and transitive package weight.
- Separate optional integrations into dedicated packages.
- Avoid leaking third-party types through core public APIs.
- Support dependency injection without requiring a specific container.
- Keep platform-specific behavior behind adapters.

### Observability
- Provide structured logging, metrics, tracing, and diagnostic hooks.
- Avoid logging sensitive data by default.
- Use stable event names and semantic attributes.
- Make failures diagnosable without enabling verbose internal logging.
- Expose health and readiness information where applicable.

### Versioning
- Follow semantic versioning for public contracts.
- Treat serialization formats, configuration keys, and emitted events as APIs.
- Deprecate before removal and document migration paths.
- Avoid behavior changes in patch releases.
- Keep generated artifacts deterministic across versions where possible.

### Testing
- Make components independently testable.
- Provide fakes or lightweight test implementations for major abstractions.
- Test concurrency, cancellation, retries, resource cleanup, and failure paths.
- Maintain compatibility and regression tests for public contracts.
- Include representative examples as executable tests.

### Documentation
- Document contracts, invariants, ownership, lifetimes, and failure semantics.
- Document the failure contract of methods and constructors, including expected exception types, triggering conditions, and whether failures may originate from delegated operations.
- Provide minimal examples for common use cases.
- Distinguish supported extension points from internal implementation details.
- Record performance characteristics and known tradeoffs.
- Keep examples aligned with the current API.



## Commit Message Rules

Generate:
1. Conventional commit title.
2. Summary bullets.
3. Architectural implications (if any).
4. Breaking changes (if any).

Focus on semantic intent over mechanical code changes.

## Pull Request Summary Rules

Include a `Critical files for review` section in every pull request description. Select the smallest useful set of
files that lets a human reviewer understand and validate the semantic change, normally three to seven files, and
order them by review value. For each file, state in one sentence which contract, invariant, algorithm, wire format,
or architectural decision deserves attention. Name files with repository-relative paths and link them when the pull
request surface supports stable file links.

Prefer semantic authorities, public contracts, core algorithms, durable schemas/serialization, nontrivial adapter
boundaries, and representative invariant tests. Do not make the section an exhaustive changed-file list. Exclude
generated files, mechanical rename consumers, formatting-only edits, and repetitive fixtures unless they introduce
independent risk or must be inspected to verify a breaking emitted contract. Summarize such mechanical or broad
changes elsewhere in the pull request without presenting every affected file as critical review material.


## Testing

Testing pyramid:
- Fine-grained and isolated unit tests for key components.
- Local integration tests that use mocked dependencies to test component interactions.
- On demand: 
  - testing scripts that construct emulators for backend services (Cosmos, Azure).
  - UI automation tests: use Playwright both directly and via compilation from Cohesive.Presentation.

Prefer:
- semantic invariant tests, property-based.
- transition correctness tests.
- process determinism test.

Avoid:
- brittle UI snapshot tests

Frontend test commands:
- Use `corepack pnpm frontend:test` for shared frontend package unit tests.
- Use `corepack pnpm frontend:build` for shared frontend package type/build checks.
- Prefer semantic presentation selectors such as `data-presentation-view-id`, `data-presentation-action-id`, `data-presentation-form-id`, and `data-presentation-flow-id` over CSS selectors in UI automation.

## Long-Term Direction

Cohesive aims to:
- support multiple host languages and runtimes (C#, TypeScript, Java, Python, Go, Rust)
- be highly efficient
- support semantic compilation into different execution targets
- preserve semantic/infrastructural separation

Favor extensibility and semantic clarity over framework convenience.

## Adoption Model

The building blocks can work piecemeal as well as together. Users should be able to adopt Cohesive incrementally:

- Start with simple foundations such as the Cohesive prelude, Cohesive.Relations, Cohesive.Configuration, etc.
- Add Cohesive configuration and CLI harness helpers where needed.
- Use Cohesive relations for DTO mapping and projection/query scenarios.
- Grow into the entity and process models.
- Adopt the semantic AI components when the problem warrants them.

Design for this gradient of adoption. The system should reward partial use and composability, not require an all-in rewrite.
