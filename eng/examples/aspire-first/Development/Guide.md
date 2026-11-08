# Orders authoring and inspection

This host exports the Aspire fulfillment model's entities, shapes, transitions, process, service, native queries and portable API contracts. Call workspace and retain snapshotId; resolve identities before
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

## Author a query and expose it as a read endpoint

1. Call workspace and keep its snapshot ID. Inspect fulfillment/reservation-availability, then use references to find its bound endpoint. The current examples are FulfillmentQueries.ReservationAvailability and OrderApi.Availability in eng/examples/aspire-first/Orders.Model.
2. In FulfillmentQueries.cs declare a typed query using Queries.WithInput<T> and Array. Filter the owning domain source by its partition and the named input. Project only the fields in the public result. Use the existing reservation entity and partition convention.
3. In OrderApi.cs declare the GET route, typed route parameter and array result. In OrderEndpoints bind the endpoint to that same query with its explicit revision, row/byte limits and matching route input. Inspect the Orders runtime host's read capability registrations as well as the offline export.
4. Rebuild and export from the root using the command above. Keep the same MCP session, obtain a fresh workspace snapshot, run check, and compare against the retained baseline. Verify the added query and endpoint, and use references on the query to verify the binding. For an added endpoint, compatibility should be Compatible for ExistingClientToProposedServer and Breaking for ProposedClientToExistingServer.
5. Exercise the bound read on at least two matching rows and one other key, and check an absent key returns an empty array. Check the returned identities as well as the count.


For the working SKU example, inspect fulfillment/reservations-by-sku and api:Order.ReservationsBySku. The route is GET /orders/reservations/by-sku/{sku}. The query asks for IndependentSources, which both Orders hosts can provide.

To verify the HTTP result with the repository's PostgreSQL fixture, set COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING to a disposable PostgreSQL test database and run:

    dotnet test src/Cohesive.Adapters.Aspire.Tests --filter FullyQualifiedName~Declared_join_returns_empty_and_multiple_reservations_with_local_partition_isolation

The test is skipped without that connection string. Confirm it ran before treating the result as behavior evidence.

## Author cancellation with recovery

Start with learn, retain workspace's snapshot, then inspect fulfillment/submit and fulfillment/release and follow their references. The owning declarations are in Orders.Model/FulfillmentDomain.cs and OrderApi.cs. Review FulfillmentProcessIntegrationTests and DurableFulfillmentIntegrationTests for native transition receipts, continuation identity, and recovery fixtures.

Author cancellation through typed transitions and ProcessModel. Declare which order states permit cancellation and when cancellation becomes complete. Read reservation identity, SKU and quantity from canonical state. Guard positive quantities and handle missing or conflicting reservation evidence explicitly. Keep intermediate progress inspectable so a failed release can be resumed. Inventory release must happen at most once across repeated commands, concurrent attempts and interrupted process occurrences. A per-occurrence receipt only deduplicates that occurrence: establish how separate command identities are rejected or share the same authoritative operation before adding stock.

Expose the command through the existing accepted-process API and result route, including explicit authorization and lifetime. Reuse native storage and receipt composition. After building, export the new transitions/process/service/endpoints and use check, compare, references and compatibility in both directions. Review existing response contracts when extending lifecycle enums. Execute real repository tests with failures injected before and after inventory release, recreate runtime preparation, and retry. Verify final order state, reservation progress, inventory totals, and results for same and distinct command identities.

The cancellation recipe supports a bounded set of reservations. Query the complete set, including released facts, and persist its observed sequence with an initial DurableCut before preflight or claim. Retain that sequence in portable recurrence state; never reconstruct release positions from a later query. Different new executions may observe different row order, while recovery uses the exact captured sequence. Validate every row and its inventory before claiming work. Use existing stateful RepeatAcrossActivation for sequential releases; each reservation gets a distinct receipt occurrence even when rows share a SKU. Persist validated release evidence with DurableCut before mutating inventory, so a crash after marking a reservation released replays that observation and the original receipts. Reject changed reservation facts instead of releasing a different quantity. Orders move Submitted -> Cancelling -> Cancelled. The original durable continuation must resume Cancelling work. Separate continuations receive InProgress, or AlreadyCancelled after completion; takeover is not supported. Released remains visible on reservation reads. This process requires durable execution; finite host execution must reject it explicitly.

Use ProcessQuery to attach a canonical bounded native query to ProcessContext.Query. The original query, row/byte bounds, consistency requirement and observational contract are exported. Use an explicit typed evidence projection with an existence flag when guards need nonnullable operands from an optional point read. Bind awaited results to locals before returning them. Use stateful bounded recurrence for sequential processing, with a portable captured sequence and cursor. Verify the final reservation count and every captured row before declaring cancellation complete; this detects observed additions or mutations but does not promise cross-entity atomicity against concurrent external writers.

For an existing Orders database apply eng/examples/aspire-first/migrations/002-reservation-cancellation.sql before host startup. The new Released column defaults to false. Run the PostgreSQL-backed CancellationIntegrationTests with COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING set. A skipped test requires a configured test database before behavior is qualified.
