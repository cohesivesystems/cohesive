# Cohesive.Presentation

`Cohesive.Presentation` is a target-independent presentation language for backend-declared views, fields, actions,
forms, navigation, data sources, flows, accessibility, and design semantics.

## Install

```bash
dotnet add package Cohesive.Presentation
```

## Declare a data source

Presentation identities are application-owned contracts used by generated frontend artifacts, routes, selectors,
tests, and bindings. They are distinct from compiler-internal node IDs:

```csharp
var shipments = new DataSourceDefinition(
    Id: "shipments",
    Name: "Shipments",
    Kind: DataSourceKind.CollectionQuery,
    ResultShape: "ShipmentSummary",
    Parameters: [new("status", "string", IsRequired: false, Label: "Status")],
    DefaultSort: [new("pickupDate", Descending: false)],
    Cache: null,
    Invalidation: null,
    Residency: ResidencyHint.Server,
    Binding: new(
        PresentationBindingKind.RelationQuery,
        Id: "queries.shipments"),
    Annotations: []);
```

The binding points to backend-owned Relation semantics. A frontend interpreter decides how to request, cache, render,
and refresh the data without creating another independent query catalog.

## What this package describes

- Applications, workspaces, routes, pages, regions, and nested views.
- Fields, collections, forms, actions, navigation, metrics, and interaction state.
- Relation/query data sources and Transition, Process, or API action bindings.
- Visibility, enablement, synchronization, invalidation, and result behavior.
- Accessibility semantics, stable automation selectors, and design annotations.
- Deterministic module composition, validation, persistence, and frontend projection inputs.

## Current boundary

The canonical presentation module is the source of truth. React components, routes, TypeScript contracts, CSS,
component-library choices, and runtime data clients are derived interpretations.

The current package exposes the complete structural record surface. Application helpers and generators may provide a
smaller conventions-driven authoring projection, but they must lower to the same module definition rather than create
another UI model.

## Continue

- [Internals](INTERNALS.md) retains the complete collection page, action, field, data-source, and module composition
  example.
- [`Cohesive.Api`](../Cohesive.Api/README.md) provides semantic endpoint declarations.
- [`Cohesive.Adapters.TypeScript`](../adapters/Cohesive.Adapters.TypeScript/README.md) emits frontend contracts.
- [Frontend packages](../frontend/README.md) contain the React rendering and design-system interpretations.
