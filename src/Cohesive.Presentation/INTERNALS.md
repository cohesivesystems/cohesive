# Cohesive.Presentation internals

This document contains the complete structural presentation example behind the [package overview](README.md).

Target-independent presentation IR for declaring navigation, workspaces, views, fields, actions, forms, flows, data sources, and design semantics.

## Install

```bash
dotnet add package Cohesive.Presentation
```

## Use When

- You want backend-declared UI semantics that can be projected into frontend runtimes.
- You need a shared model for views, actions, forms, navigation, and data-source binding.
- You want presentation identifiers, selectors, routes, and actions to come from a semantic source of truth.

## Example

```csharp
using Cohesive.Presentation;

var shipments = new DataSourceDefinition(
    Id: "shipments",
    Name: "Shipments",
    Kind: DataSourceKind.CollectionQuery,
    ResultShape: "ShipmentSummary",
    Parameters: [new("status", "string", IsRequired: false, Label: "Status")],
    DefaultSort: [new("pickupDate", Descending: false)],
    Cache: null,
    Invalidation: new(DataSourceIds: ["shipments"], ActionIds: ["assign-carrier"], EntityIds: ["Load"]),
    Residency: ResidencyHint.Server,
    Binding: new(PresentationBindingKind.RelationQuery, Id: "queries.shipments"),
    Annotations: []);

var assignCarrier = new ActionDefinition(
    Id: "assign-carrier",
    Name: "Assign carrier",
    Kind: ActionKind.TransitionAction,
    Scope: ActionScopeKind.Row,
    Binding: new(PresentationBindingKind.Transition, TransitionId: "Load.AssignCarrier"),
    Parameters: [new("loadId", "string", IsRequired: true)],
    Preparation: null,
    Enablement: [],
    Execution: new(ActionExecutionMode.Immediate, IsLongRunning: false, RequiresConfirmation: false),
    EndpointRequests: [],
    Result: new(InvalidateDataSourceIds: ["shipments"], NavigateToRouteId: null, Toast: "Carrier assigned"),
    Design: null,
    Accessibility: null,
    Annotations: []);

var page = new ViewDefinition(
    Id: "shipments-page",
    Name: "Shipments",
    Kind: ViewKind.Page,
    Subject: new(ViewSubjectKind.DataSource, DataSourceId: "shipments"),
    DataSourceIds: ["shipments"],
    Regions: [],
    FieldIds: ["load-id", "status", "carrier"],
    Actions: [new("assign-carrier", Region: "toolbar", Label: "Assign carrier")],
    Chrome: new(
        Title: new(PresentationValueKind.Literal, Literal: "Shipments"),
        Subtitle: null,
        Collapsible: false,
        CollapseStateId: null,
        Slots: []),
    State: [],
    Synchronization: [],
    InteractionStateId: null,
    Accessibility: null,
    Design: null,
    Annotations: [],
    Collection: new(
        Chrome: new(
        [
            new CollectionChromeSlotDefinition(
                Id: "grid",
                Name: "Shipment grid",
                Kind: CollectionChromeSlotKind.Body,
                Placement: CollectionChromeSlotPlacement.Inline,
                DataSourceIds: ["shipments"],
                QueryFormId: null,
                DetailViewId: null,
                ActionIds: ["assign-carrier"],
                RowActions:
                [
                    new(
                        Id: "assign-carrier-row",
                        ActionId: "assign-carrier",
                        Kind: CollectionRowActionKind.Primary,
                        Label: "Assign",
                        Icon: "truck",
                        Order: 0,
                        Parameters:
                        [
                            new("loadId", ValuePath: "loadId", FieldId: "load-id", OmitWhenNull: true, Annotations: [])
                        ],
                        IsEnabled: null,
                        IsVisible: null,
                        Annotations: [])
                ],
                SelectionActions: [],
                Columns:
                [
                    new("load", "load-id", ValuePath: "loadId", IsVisible: true, Order: 0, Width: "12rem", Annotations: []),
                    new("status", "status", ValuePath: "status", IsVisible: true, Order: 1, Width: "10rem", Annotations: []),
                    new("carrier", "carrier", ValuePath: "carrierName", IsVisible: true, Order: 2, Width: "16rem", Annotations: [])
                ],
                RowIdentityPath: "loadId",
                RowLabelPath: "loadId",
                SelectionMode: CollectionSelectionMode.Single,
                ActivatedRowActionId: null,
                ActivateOnRowClick: false,
                SelectOnRowClick: true,
                DetailActivation: null,
                Title: null,
                EmptyMessage: "No shipments",
                ClearSelectionOnQueryChange: true,
                FieldIds: ["load-id", "status", "carrier"],
                StateId: null,
                Value: null,
                Annotations: [])
        ],
        Annotations: []),
        Annotations: []));

var module = PresentationModuleComposer.Compose(
    id: "dispatch",
    name: "Dispatch",
    version: "1",
    new PresentationModuleContribution
    {
        DataSources = [shipments],
        Fields =
        [
            new("load-id", "loadId", "Load", null, FieldDisplayKind.Text, FieldEditKind.ReadOnly, null, null, null, ["sort"], null, null, []),
            new("status", "status", "Status", null, FieldDisplayKind.Status, FieldEditKind.ReadOnly, null, null, null, ["filter"], null, null, []),
            new("carrier", "carrierName", "Carrier", null, FieldDisplayKind.Text, FieldEditKind.ReadOnly, null, null, null, [], null, null, [])
        ],
        Actions = [assignCarrier],
        Views = [page]
    });
```

## Related Packages

- `Cohesive.Api` for action endpoint declarations.
- `Cohesive.Adapters.TypeScript` and the `@cohesivesystems/*` npm packages for frontend projection and rendering.
