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
