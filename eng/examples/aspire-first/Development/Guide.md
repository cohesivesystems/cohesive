# Orders authoring and inspection

This host exports the actual Aspire fulfillment model's four transitions and its process. It does not start
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
with Available present means stale revision/fingerprint. Review IncompleteDefinitions; extension-owned and
unregistered references are not proven absent. Reference lookup is direct, not transitive impact analysis. No callback or hand-edited IR becomes authoritative.

Workflow: learn -> workspace -> resolve -> inspect -> ordinary source edit -> export -> workspace -> check ->
compare against the original snapshot. Supply expectedSnapshot for inspection, resolution, validation and
comparison. A nonempty changedInputs means validation is stale. Missing domain validation, errors and warnings
must be reviewed; check is not a runtime correctness or cross-document compatibility proof.

The input boundary conservatively includes discovered C# build files under src/Cohesive* and src/adapters, Orders.Model and this exporter,
plus Directory.Build.props, nuget.config and this guide. This can invalidate the workspace for unrelated library edits.
It does not attest the loaded binary, installed SDK, package cache, undeclared environment or external inputs.
Discovery does not detect new files until re-export. Export from a fixed source tree for reproducibility.

The query declarations are relation documents, outside this execution-document export. API routes, generated
clients, entity graphs, target capabilities and storage mappings are not indexed here.
Use the existing application explain command and source search for those concerns. The next integration step
is linking these owning-domain documents; inventing parallel models in this host would hide the gap.
