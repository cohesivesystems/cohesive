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

A project that is itself a lowering pipeline, an IR serializer or persistence layer, or a compiler test, fixture,
or benchmark opts in explicitly in its project file, with the reason:

```xml
<PropertyGroup>
  <CohesiveIrProducer>Relations compiler tests over hand-built IR</CohesiveIrProducer>
</PropertyGroup>
```

`Directory.Build.targets` turns the property into `[assembly: IrProducer(reason)]`. Outside this repository, declare
the attribute directly. There is no exemption by project name: every producer is listed, and
`git grep CohesiveIrProducer` shows them all.

## What the rule does not cover

COHIR001 makes the supported route the easy one; it does not make other routes impossible. It reports `new`
(including target-typed `new`) and `with` on a marked type or a type derived from one. It does not see:

- generic construction through a type parameter (`new T()`);
- reflection or `Activator.CreateInstance`;
- deserializing hand-written IR documents;
- frontends in languages other than C#.

Inside the declaring assembly the rule does not apply at all, since that assembly owns lowering and serialization.
