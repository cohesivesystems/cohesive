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
