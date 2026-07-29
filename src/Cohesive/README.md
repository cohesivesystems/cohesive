# Cohesive

Core primitives, shape metadata, domain quantities, code generation abstractions, and prelude helpers shared by the Cohesive package family.

## Install

```bash
dotnet add package Cohesive
```

## Use When

- You need the common shape model used by Cohesive blocks and adapters.
- You want shared domain primitives such as typed quantities, codes, identifiers, paths, and observation values.
- You are building a new Cohesive block or adapter and need the base semantic contracts.

## Example

```csharp
using Cohesive.Model;

[ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
public sealed record Shipment(string Id, IReadOnlyList<Stop> Stops);

[ShapeType("type.stop")]
public sealed record Stop(string City, string State);

var graph = new ClrShapeGraphBuilder()
    .AddShape<Shipment>()
    .Build(new("shipping"));
```

## Package Role

`Cohesive` is the foundation package. Higher-level blocks such as `Cohesive.Relations`, `Cohesive.Transitions`, `Cohesive.Processes`, `Cohesive.Presentation`, and `Cohesive.Api` depend on it.

## Canonical execution interactions

`Cohesive.Execution` separates reusable interaction contracts from their runtime emissions.
`InteractionContractDefinition` is the persisted semantic authority, carried by the shared
`ExecutionDefinitionDocument`; `InteractionEnvelope` is a versioned runtime value that references one exact,
typed contract revision and carries its stable emission identity, origin, correlation and causation, authority
scope, idempotency basis, ordering, delivery demands, and provenance. Payloads and terminal values are
materialized `PortableValue` instances rather than CLR or transport values, and their exact referenced contracts
carry the explicit schema revisions.

The contract and envelope algebras are closed over four kinds:

- A domain event records a fact and creates no emitter-side response obligation.
- A Request creates a typed terminal response obligation and names where that response is consumed.
- A Signal is an addressed one-way input with no response obligation.
- A Reply identifies the Request emission it discharges and carries one declared result, failure, timeout, or
  cancellation outcome.

Request contracts make terminal variants, timeout and cancellation support, retry and ambiguous-outcome
semantics, late/stale/duplicate-result disposition, retention, and unresolved-result handling explicit. A Request
envelope carries an exact semantic address for either a durable Process token or a Transition continuation; those
alternatives are distinct types rather than transport addresses or loosely interpreted strings. The current
interaction reader validates contract, payload, and Reply-outcome links. Definition/node resolution for Process
tokens, Transition origins, and Transition continuation addresses belongs to the Process/Transition compiler and
durable result-admission work, where the referenced definitions and live continuation state are available.

Persistence events are not interaction kinds. Change-feed records, checkpoint history, and outbox storage records
describe reconstruction, audit, and delivery mechanisms; an adapter must not treat them as domain events unless a
canonical domain-event contract and envelope explicitly provide that meaning.

`InteractionContractDocuments`, `InteractionContractCatalog`, `InteractionEnvelopeValidator`, and
`InteractionEnvelopeJsonSerializer` enforce exact schema, revision, fingerprint, discriminator, payload, and Reply
outcome links with structured diagnostics; envelope admission requires the linked catalog rather than trusting a
wire discriminator in isolation. The existing `EffectRequest`, delegate-bound continuations, raw Process
signal payloads, and `EntityOutboxMessage` types remain migration surfaces for current runtimes. They may produce or
carry canonical interactions, but they are not a parallel semantic authority; durable dispatch, inbox/outbox,
definition/node resolution, deduplication, acknowledgement, retry/timeout triggering, and reconciliation or
escalation path enforcement belong to subsequent compiler and runtime work.

## Expression IR and Analysis

`Expr` is the portable, non-generic expression IR shared by Cohesive languages. An expression
describes a computation, but it does not persist the CLR object, query row, transition state, or
other runtime context in which that computation happens.

An expression site supplies that missing semantic boundary. It identifies where an expression is
used, declares the bindings, parameters, current item, and ambient capabilities available there,
and states the expected result contract. Shared expression analysis then derives the bindings,
field paths, parameters, current-item access, functions, operators, and ambient capabilities the
expression requires. The immutable result can be consumed by validators, backend compilers,
lineage tools, input-contract analysis, and diagnostics without annotating or changing the
canonical expression.

Available scope and derived requirements intentionally remain separate:

- A scope states what a particular site provides.
- Requirements state what the expression reads or otherwise needs.
- A semantics catalog describes functions and operators independently of an interpreter.
- A capability profile states which operations a language surface allows or a selected interpreter can realize.
- Runtime evaluation contexts remain private to their interpreters.

A constrained coarse category, such as Boolean or Numeric, requires a present, non-null value unless
the expectation also supplies a value contract that explicitly permits absence or null. This keeps
operator and function null semantics deterministic instead of inheriting interpreter-specific coercion.
Conversely, bare `TypeRef` metadata on a call or aggregate declares its portable type but does not
invent presence or nullability guarantees; a semantics-catalog result contract may provide stronger
guarantees when the operation defines them.

Within a function argument declared as current-item scoped, an unbound field path rooted at
`item` reads that current item: `item.Name` addresses its `Name` field, while `CurrentItemExpr`
represents the whole item. Parameter scope separately records whether an invocation may omit a
parameter and the guarantees of the value that is evaluated after defaults are applied.
Each derived field requirement identifies its root as a named binding, the scoped current item,
or unresolved, so acquisition and lineage consumers do not have to infer root semantics from a
nullable binding identifier.

The same `Expr` may therefore be analyzed under several scopes and capability profiles. This is
why the canonical IR remains non-generic: C#, TypeScript, SQL, document-query, graph-query, and
in-memory interpreters can share one persisted expression while host-language authoring helpers
lower immediately to that representation.
