# Cohesive.Analyzers

Roslyn analyzers and source generators that support Cohesive authoring patterns.

## Install

```bash
dotnet add package Cohesive.Analyzers
```

## Use When

- You want source generation for Cohesive discriminated unions, code sets, quantity wrappers, or expression-first canonical Process authoring.
- You want analyzer feedback for Cohesive domain authoring conventions.
- You are building libraries that use Cohesive semantic authoring patterns and want compile-time assistance.

## Example

```csharp
using Cohesive.Domain;

[CodeSet]
public static partial class ShipmentStatus
{
    public const string Draft = "draft";

    [CodeSet("Dispatched", Description = "Shipment has left the origin facility.")]
    public const string Dispatched = "dispatched";
}
```

## Notes

This package is consumed as an analyzer package. It does not provide runtime APIs for application code.

For Process authoring, `GenerateProcessDefinition` lowers a syntax-only C# `async ProcessTask<T>` method to a
generated `Define` factory over canonical persisted IR. See the
[`Cohesive.Processes` authoring guide](../Cohesive.Processes/README.md) for the executable example, supported
constructs, identity compatibility boundary, and the distinction between authoring source and restored execution.

Generated code-set `All`, `Definitions` and enum `GetAll()` catalogs expose read-only collection storage:
callers cannot recover and mutate the backing array or write through `IList`. The declarations remain
authoritative, and the existing `IReadOnlyList` signatures, order, values and metadata are unchanged.
This protects consumers that prepare deterministic interpretations of constant code-set metadata.
The guarantee applies to collection storage; it does not make an arbitrary reference-valued code immutable.

`COHIR001` reports direct construction (`new` or `with`) of canonical IR types marked `[LoweringOutput]`, such as
Relations definitions and logical query nodes, outside the assembly that declares them. Source is the authoring
authority and the IR is derived from it, so other code obtains these types from an authoring API or a serializer.
Lowering pipelines, serializers, and test, fixture, or benchmark assemblies declare `[assembly: IrProducer(reason)]`;
this repository applies it to test, fixture, and benchmark projects through `Directory.Build.targets`. See
[COHIR001](docs/COHIR001.md).
