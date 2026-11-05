# Orders authoring and inspection

This host exports the actual Aspire fulfillment model's transitions, process, service, native query documents and API endpoint inspection projections. It does not start
Aspire, databases, workers or containers. Call workspace and retain snapshotId; resolve identities before
inspection. Static learn guidance works before the first export.

Build and export from the repository root:

    dotnet run --project eng/examples/aspire-first/Development -- export "$PWD"

The derived artifact is tmp/orders-development.json. Rebuild/re-export after editing; do not use --no-build for
export after source changes. The server reads that artifact and never builds or executes application operations.

Authoring authority is eng/examples/aspire-first/Orders.Model/FulfillmentDomain.cs. Existing document identities
include example/order/submit, fulfillment/reserve, fulfillment/release, fulfillment/reservation/create and
fulfillment/submit. Inspect sourceMappings for repository-relative path/startLine/endLine values. The Orders project explicitly
maps its compiler source root to /_/; the transition and process producers retain declaration-line locations.
These are inclusive one-line spans, not complete expression character ranges. Unmapped producers may supply
no location; do not infer one from the description. The root view lists mappings; narrower pointers select
matching source references. Source locations remain attribution, not access authorization.

For a behavior change, edit the typed transition or generated-process source and update its authored revision.
Call references with the transition identity and expectedSnapshot to discover direct consumers; a process captures exact transition references, so re-export and compare
all definitions, not just the edited transition. A null Available target means missing, and IsExactMatch false
requires review: compare IsContentMatch and the optional revision evidence. Native query documents have no authored revision. Review IncompleteDefinitions; extension-owned and
unregistered references are not proven absent. Use impact for bounded reverse reachability and shortest evidence paths; review depthLimitReached and resultLimitReached. No callback or hand-edited IR becomes authoritative.

Workflow: learn -> workspace -> resolve -> inspect -> ordinary source edit -> export -> workspace -> check ->
compare against the original snapshot. Supply expectedSnapshot for inspection, resolution, validation and
comparison. A nonempty changedInputs means validation is stale. Missing domain validation, errors and warnings
must be reviewed; check is not a runtime correctness or cross-document compatibility proof.

The input boundary conservatively includes discovered C# build files under src/Cohesive* and src/adapters, Orders.Model and this exporter,
plus Directory.Build.props, nuget.config and this guide. This can invalidate the workspace for unrelated library edits.
It does not attest the loaded binary, installed SDK, package cache, undeclared environment or external inputs.
Discovery does not detect new files until re-export. Export from a fixed source tree for reproducibility.

Query source is eng/examples/aspire-first/Orders.Model/FulfillmentQueries.cs; bindings and routes are in OrderApi.cs.
Inspect fulfillment/order-details, then references and impact to discover its actual api:-prefixed endpoint.
The exercise changes the Orders query filter to select Submitted orders, rebuilds, and checks that the query
and endpoint binding projection change. Query source locations are not supplied by this producer; use these owning files.

Resolve AvailableStock, use impact on the qualified field to trace the Orders query and api:Order.Details, then compare portable API contracts after rebuilding. The exercise checks both an additive inventoryPartition and a rename from availableStock to remainingStock with generated JSON readers/writers.

The portable API document retains HTTP bindings, result variants, serializer profile, qualified core types and execution/service references. Use `compatibility` with an endpoint ID, retained baseline snapshot and fresh expected snapshot. The default direction is existing client to proposed server; the reverse direction tests the proposed client against the existing server. Under `http-json-tolerant-readers/v1`, requests must remain acceptable to the server and responses readable by the client. Findings identify wire paths and return Compatible, Breaking or Unknown. Custom serializer behavior, authorization changes and unsupported type semantics can prevent a proof. This policy does not qualify arbitrary clients, deployed routes, or runtime behavior.
