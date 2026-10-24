# COHIR001: Canonical IR constructed outside lowering

Source is the authoring authority in Cohesive, and the canonical IR is derived from it by lowering. Types marked
`[LoweringOutput]` (relation and query definitions, logical query nodes, hosted query definitions) are produced by:

- lowering pipelines, such as the Relations expression and structural authoring APIs;
- IR serialization and persistence;
- compiler tests, fixtures, and benchmarks.

This diagnostic is reported when another assembly constructs one of these types with `new`, or copies one with
`with`.

## How to fix

Author the definition as source, through the expression API (`RelationQuery.Expression()`) or the structural core
(`RelationQuery.Structural()`), and obtain the definition from `BuildQuery` or `BuildRelation`. Importers and
generators emit source the same way, so their output is reviewed like any other change.

Value objects that authoring APIs take as arguments, such as page or ordering definitions, are not marked and do
not trigger this diagnostic. Prefer the fluent operators where they exist (for example `Take` rather than an explicit
offset page).

## When the assembly is a legitimate producer

An assembly that is itself a lowering pipeline, an IR serializer or persistence layer, or a test, fixture, or
benchmark project declares:

```csharp
[assembly: IrProducer("compiler tests")]
```

Test, fixture, and benchmark projects in this repository receive the declaration from `Directory.Build.targets`.
