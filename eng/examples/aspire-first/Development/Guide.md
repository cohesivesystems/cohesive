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
fulfillment/submit. At /definition, source attribution can be only a semantic identity. Inspect /metadata/sourceMap for
compiler descriptions containing filename/line, and /definition/preconditions/1 for the reserve guard's
specific reference. This model uses basename/line descriptions and semantic references, not structured
repository-relative source spans. Locate the declared file above with source search.

For a behavior change, edit the typed transition or generated-process source and update its authored revision.
Review consumers in the same model; a process captures exact transition references, so re-export and compare
all definitions, not just the edited transition. No callback or hand-edited IR becomes authoritative.

Workflow: learn -> workspace -> resolve -> inspect -> ordinary source edit -> export -> workspace -> check ->
compare against the original snapshot. Supply expectedSnapshot for inspection, resolution, validation and
comparison. A nonempty changedInputs means validation is stale. Missing domain validation, errors and warnings
must be reviewed; check is not a runtime correctness or cross-document compatibility proof.

The input boundary conservatively includes discovered C# build files under src/Cohesive* and src/adapters, Orders.Model and this exporter,
plus Directory.Build.props, nuget.config and this guide. This can invalidate the workspace for unrelated library edits.
It does not attest the loaded binary, installed SDK, package cache, undeclared environment or external inputs.
Discovery does not detect new files until re-export. Export from a fixed source tree for reproducibility.

The query declarations are relation documents, outside this execution-document export. API routes, generated
clients, entity graphs, target capabilities, storage mappings and reverse dependencies are not indexed here.
Use the existing application explain command and source search for those concerns. The next integration step
is linking these owning-domain documents; inventing parallel models in this host would hide the gap.
