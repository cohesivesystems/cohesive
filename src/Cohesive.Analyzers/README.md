# Cohesive.Analyzers

Roslyn analyzers and source generators that support Cohesive authoring patterns.

## Install

```bash
dotnet add package Cohesive.Analyzers
```

## Use When

- You want source generation for Cohesive discriminated unions, code sets, quantity wrappers, or process flow authoring.
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
