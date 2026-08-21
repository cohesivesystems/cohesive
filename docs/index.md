---
kind: explanatory
status: accepted
authority: cohesive.documentation-index
owners: [cohesive-core]
applies_to: [cohesive]
last_verified: 2026-08-12
supersedes: []
---

# Cohesive Documentation

This index is the entry point to the architectural documentation for Cohesive. Cohesive is a family
of human-oriented semantic languages with evolving realization mechanisms. Agents and other tools
are expected to produce most portable intermediate representations; people use the languages and
their projections to express and calibrate intent, understand change, and judge evidence.
Compilers, interpreters, and agents project that meaning across execution, validation, verification,
documentation, migration, and operations.

The repository root [README](../README.md) is the package and contributor entry point. Package
READMEs describe implemented APIs close to their source. The documents below explain the system-wide
vision, semantic contracts, language relationships, representative use cases, and conformance
expectations.

## Start here

| Question | Document | Role |
| --- | --- | --- |
| Why should Cohesive exist, and what future is it trying to create? | [Cohesive vision](vision/cohesive-vision.md) | Direction and non-goals |
| What is the economic and adoption case for adding a semantic layer? | [Why use Cohesive blocks?](WHY_COHESIVE.md) | Adoption decision guide |
| What counts as canonical semantics, and what is only a producer or interpretation? | [Semantic model](concepts/semantic-model.md) | Normative conceptual contract |
| How do the Cohesive languages divide responsibility and compose? | [Language family](architecture/language-family.md) | Architectural ownership map |
| How should implementation quality be judged when desirable properties conflict? | [Code quality and optimization](quality/code-quality.md) | Normative decision model |
| What does the architecture look like in complete application scenarios? | [Golden verticals](use-cases/golden-verticals.md) | End-to-end examples and intended evidence |
| How are semantic and target claims tested? | [Conformance](quality/conformance.md) | Verification strategy |
| Which Execution Kernel behavior exists today? | [Execution Kernel compatibility](EXECUTION_KERNEL_COMPATIBILITY.md) | Implementation compatibility inventory |
| How do I adopt, execute, observe, and migrate to the canonical kernel? | [Execution Kernel adoption guide](EXECUTION_KERNEL_GUIDE.md) | Source-backed implementation guide and executable examples |
| How is index synchronization operated? | [Index synchronization runbook](INDEX_SYNC_RUNBOOK.md) | Operational procedure |

## Sources of authority

Cohesive uses several kinds of artifact, each with a different responsibility:

1. **Canonical persisted IR is semantic authority for a materialized model.** Authoring DSLs,
   inference systems, importers, and generators produce the IR; they do not become parallel sources
   of truth.
2. **Normative architectural documents define repository-wide vocabulary and contracts.** They
   constrain implementations until executable contracts can carry the meaning directly.
3. **Public APIs and durable serialization formats are executable contracts.** Their tests and
   compatibility rules must agree with the canonical semantic documents.
4. **Package READMEs document implemented surfaces.** They may be narrower than the long-term
   architecture and must identify important gaps.
5. **Compatibility inventories and maturity reports describe current coverage.** Missing behavior
   remains missing; an inventory does not redefine the intended semantics.
6. **Decision records explain choices and consequences.** They may refine a contract but do not
   silently replace it.
7. **Plans and issue trackers coordinate work.** Identifiers such as `ARI-180` or `COH-27` are
   provenance, not semantic names or durable specifications.
8. **Generated documentation is a derived interpretation.** It must identify its source IR and
   generator and must not be edited as an independent authority.

When two authorities appear to disagree, stop and resolve the disagreement in the lowest document
or executable contract that owns the shared meaning. Do not preserve both descriptions with an
implicit conversion or duplicated case list.

## External and private specifications

Private specifications in Notion or another access-controlled system are acceptable during R&D,
including as working normative specifications, when all of the following are true:

The current cross-cutting working specifications are:

| Specification | Authority | Owner |
| --- | --- | --- |
| [Cohesive Building Block Tenets](https://app.notion.com/p/3aa8cf7881f981a1a7f6fec5e6a099ed) | Normative architectural guidance | `cohesive-core` |
| [Cohesive Agentic Development](https://app.notion.com/p/39d8cf7881f980aba97af97452caa073) | Working agentic-development architecture | `cohesive-core` |
| [Cohesive Change Model Specification](https://app.notion.com/p/3aa8cf7881f981bdaa4dd87a75eddcb5) | Working change-design foundation; shared IR provisional | `cohesive-core` |

- the repository records a stable title or identifier, owner, authority status, and link;
- the repository contains enough of the contract's purpose, scope, invariants, and non-goals for a
  contributor to understand why the implementation exists;
- agents and reviewers assigned work that depends on the specification have access, or receive an
  authorized task-scoped extract;
- tests, persisted schemas, public API documentation, diagnostics, or decision records capture the
  externally observable consequences of the specification;
- a private document is not the only description of a public package contract that downstream users
  must implement; and
- implemented decisions are reflected back into repository-owned artifacts before the task is
  considered complete.

An agent with Notion access may use the private specification directly and cite it in a plan or pull
request. An agent without access must not guess what it says. The work should instead be scoped to
repository evidence, supplied with an authorized extract, or blocked pending access.

This policy permits private product context without making the codebase unintelligible to offline
tools, external contributors, or future agents. Long-lived portable semantics should migrate toward
repository-owned normative documents or executable IR contracts as their boundaries stabilize.

## Document roles

Documentation should declare one of these roles in its front matter:

| Kind | Meaning |
| --- | --- |
| `normative` | Defines semantics, guarantees, policy, or a durable contract. |
| `explanatory` | Teaches or motivates normative material without replacing it. |
| `decision` | Records a chosen alternative, its context, and its consequences. |
| `plan` | Coordinates future or active work and may change freely until accepted. |
| `runbook` | Describes an operational procedure and its safety boundaries. |
| `generated` | Is derived from another authority and identifies its provenance. |

Recommended lifecycle states are `draft`, `accepted`, `implemented`, and `superseded`. `Accepted`
means the document expresses intended direction; it does not imply that every described capability
is implemented. Implementation status belongs in package documentation, compatibility inventories,
tests, and eventually a repository-wide maturity matrix.

## Accepted decisions

| Decision | Status | Scope |
| --- | --- | --- |
| [Durable Task as a parallel Process interpreter](decisions/durable-task-process-interpreter.md) | Accepted; sequential execution and durable Request recovery slice implemented | Canonical Process authority, target capability inventory, Durable Task interpretation, conformance and alternatives |
| [Execution-control API package boundary](decisions/execution-control-api-package-boundary.md) | Implemented | Generic API dependency direction, execution API composition, and transport ownership |
| [ASP.NET API adapter boundary](decisions/aspnet-api-adapter-boundary.md) | Implemented | Portable API authority, Minimal API projection ownership, and package dependency direction |
| [Reconciliable training submissions](decisions/reconciliable-training-submissions.md) | Implemented | Stable logical submission identity, exact request binding, provider reconciliation, and conflict semantics |
| [Typed Request outcome projection](decisions/typed-request-outcome-projection.md) | Implemented | Canonical Request authority, exhaustive source-only C# cases, typed Effect lowering, and native-union migration boundary |
| [Typed durable Request handlers](decisions/typed-durable-request-handlers.md) | Implemented | Exact-reference adapter routing, typed handler projection, explicit target capabilities, and reconciliation evidence |

## Package documentation

The principal package entry points are:

- [Core shapes, expressions, and execution contracts](../src/Cohesive/README.md)
- [Relations](../src/Cohesive.Relations/README.md)
- [Transitions](../src/Cohesive.Transitions/README.md)
- [Processes](../src/Cohesive.Processes/README.md)
- [API](../src/Cohesive.Api/README.md)
- [Execution-control API](../src/Cohesive.Api.Execution/README.md)
- [Presentation](../src/Cohesive.Presentation/README.md)
- [Storage](../src/Cohesive.Storage/README.md)
- [AI](../src/Cohesive.AI/README.md)
- [Identity](../src/Cohesive.Identity/README.md)
- [Configuration](../src/Cohesive.Configuration/README.md)
- [Host integration](../src/Cohesive.Host/README.md)
- [Frontend packages](../src/frontend/README.md)

Adapters document their target capabilities and operational requirements beside their source under
`src/adapters/Cohesive.Adapters.*`.

## Documentation maintenance rules

- Prefer links to the semantic authority over copied definitions.
- Give concepts semantic names; retain issue identifiers only as provenance.
- State whether a claim is implemented, planned, or target-dependent.
- Keep public and protected API documentation synchronized with behavior.
- Update golden verticals when a change alters an end-to-end contract.
- Update conformance fixtures when an interpreter or adapter claims new capability.
- Record significant architectural choices under `docs/decisions/` once that directory is needed.
- Keep `AGENTS.md` short: it should map agents to these authorities and state operational rules, not
  repeat the architecture.
