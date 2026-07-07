# Cohesive Agent Notes

This repository is in an early R&D phase.
Optimize for learning, iteration speed, and sharp architectural progress. 
Move fast and break things when that helps validate the right semantic model, but keep the code understandable enough that good ideas can be retained and generalized.
Maintain a single source of truth for data, types, and behaviors.
Favor a declarative approach to building reusable components.
Frontend views and capabilities should be declared on the backend and projected onto the frontend.
The foundational paradigm is to isolate the semantics and attach various interpretations to infrastructure.

## What We Are Building

Cohesive is a toolchain for semantic system definition and orchestration of existing infrastructure. 
The goal is to define the semantics of the problem at hand first, then attach interpretations of those semantics in terms of concrete infrastructure such as storage, messaging, caches, runtimes, AI training systems, tensor operations, and mathematical computation.

The core output of this repository is not a single app. It is a set of high-quality, reusable building blocks in library form. Those building blocks should aim for:

- Wide applicability across products and domains.
- High quality and strong conceptual integrity.
- High performance, especially low allocation, batching, buffering, caching, asynchrony, and pooling where appropriate.
- Modularity and reusability rather than one-off product code.

## Architectural Stance

An underlying principle of the system is language-oriented programming. 
We define semantic constructs in the form of an IR and provide interpretations for them. 
The semantic surface should lead; infrastructure concerns should attach to that surface instead of dictating it.

Cohesive should not collapse to the lowest common denominator of the infrastructure it integrates with. 
Instead, it should model the transitive closure of capabilities and expose a first-class capability model so higher-level semantics can target what the underlying systems can actually do.

When designing abstractions:

- Start from meaning, not transport, or vendor APIs.
- Prefer explicit semantic models over ad hoc glue code.
- Keep infrastructure-specific behavior in interpreters/adapters.
- Preserve capabilities when projecting semantics onto concrete systems.
- When backend IR is the source of truth for identifiers, shapes, routes, actions, selectors, roles, workflows, states, permissions, or other semantic data, favor generated codegen artifacts over hand-written strings or duplicated frontend models. If a generated artifact is missing, extend the codegen path rather than introducing a parallel constant unless the duplication is explicitly temporary and documented.

## Repository Shape

The reusable toolchain is developed primarily through the `Cohesive.*` libraries:

- `src/Cohesive`: core shape model, primitives and prelude.
- `src/Cohesive.Configuration`: configuration building blocks (configuration profiles, rich dependency selection, etc.).
- `src/Cohesive.Relations`: relationship, projection, and query/aggregation semantics.
- `src/Cohesive.Transitions`: entity transitions and invariants.
- `src/Cohesive.Processes`: multistep workflows that can involve entity transitions, queries, waits, or arbitrary effects.
- `src/Cohesive.Presentation`: a UI/presentation layer language that is compiled/projected onto concrete UI rendering systems like React/Blazor/Angular, etc.
- `src/Cohesive.AI`: semantic AI, inference, training, vectors, numerics, and text-oriented components.
- `src/Cohesive.Api`: API declaration language that can generate OpenAPI/GraphQL/gRPC/etc.
- `src/Cohesive.Storage`: generic storage abstractions.
- `src/frontend/*`: TypeScript incarnations of Cohesive.Presentation as well as IR projection and rendering modules for React, component libraries, styling libraries, etc.
- `src/Cohesive.Adapters.*`: adapters to external infrastructure.

Adapters to external infrastructure belong in the `Cohesive.Adapters.*` projects under `src/adapters`. 
That is where integration with external storage, runtimes, ML systems, transport layers, and other concrete platforms should live.

## Documentation Expectation

- Reusable components should be well-documented. 
- Public APIs, semantic models, adapter boundaries, and performance-sensitive behavior should be explained clearly enough that other contributors can extend the system without reverse-engineering intent from implementation details alone.
- Document record ctor parameters, enum cases, method parameters unless clear from context.


## Coding Style
- Use modern C# features (collection literals/expressions, immutable collections, records, nullables, extension members/methods, switch expressions, lambda syntax, etc.).
- Favor collection expressions over explicit collection constructions (e.g., `[..items]` vs `items.ToArray()`)
- Explicitly name method parameters for primitive types or when ambiguous.
- Avoid magic strings, magic numbers. Project everything from a single source of truth.
- Type local factory/sample values against generated contracts or narrow explicit interfaces instead of relying on anonymous inferred object shapes.
- Avoid external dependencies unless discussed and approved.
- Reflection metadata access should be centralized and cached.

Hot paths:
- Minimize allocations
- Buffer pooling
- in, ref, out modifiers
- Batching
- ValueTask
- Struct-of-arrays


## Commit Message Rules

Generate:
1. Conventional commit title.
2. Summary bullets.
3. Architectural implications (if any).
4. Breaking changes (if any).

Focus on semantic intent over mechanical code changes.


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
- be highly efficient (space & time)
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
