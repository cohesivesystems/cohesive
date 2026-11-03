# Fulfillment: compose guarantees, then choose realizations

One domain declares orders, inventory and reservations. One process reserves stock, submits the order
and creates the reservation. One rooted relation derives order details for both SQL reads and asynchronous
materialization. The HTTP contract stays the same when its read realization changes.

| Decision | Declared in | Realized by |
| --- | --- | --- |
| Accept now, poll for completion | `OrderApi.Fulfill` | Linked admission/result endpoints |
| Recover accepted work across worker replacement | Service execution policy | Durable Task interpreter and native scheduler |
| Commit one transition and its replay receipt atomically | Storage attachments | PostgreSQL transaction; Marten participates when attached |
| State or events determine entity state | `FulfillmentHistoryPolicy` | Existing reconstitution and Marten history adapter |
| Query current relational state | `FulfillmentQueries.OrderDetailsRelation` | PostgreSQL statement |
| Serve an explicitly eventual read model | Storage read selection | Same relation hydrated into a pinned Elasticsearch generation |
| Return to live reads | `OrderReads.Source=Live` | Original PostgreSQL reader, with no Elasticsearch dependency |

These choices do not imply a transaction across the entire process, a cross-store snapshot, or
read-your-writes from Elasticsearch. CDC observes relational writes; it does not turn the read model
into the authoritative event stream. Event-authoritative reconstitution remains a separate storage decision.

## Run the complete proof

With .NET 10 and Docker available, from the repository root:

```sh
bash eng/test-fulfillment-composition.sh -m:4
```

The script creates isolated PostgreSQL, Durable Task and Elasticsearch containers on dynamic loopback ports,
uses synthetic data and removes its containers/volumes on exit. It does not use application connection strings.
The same script runs in CI. It verifies both State and Events authority:

1. Launch the actual worker and use its order/fulfillment HTTP endpoints.
2. Accept fulfillment, retain the receipt/result through host replacement, and retry the same command.
3. Interrupt after stock commits but before activity acknowledgement. Recover with stock reduced once.
4. Rebuild a shadow baseline **before** fulfillment, then consume its PostgreSQL changes through CDC.
5. Compare the converged generation with the live relation, select eventual reads through the storage model,
   and read `/orders/{id}/details` through HTTP.
6. Start a live-read host without registering Elasticsearch clients and read the same result from PostgreSQL.

For an order requesting two units from stock five, the result is a Submitted order, one reservation of two,
and available stock three. Comparison admits a particular observed cut; subsequent writes require further catch-up.

## Inspect the same declarations without starting resources

```sh
dotnet run --project eng/examples/aspire-first/AppHost -- explain > state.json
dotnet run --project eng/examples/aspire-first/AppHost -- explain -- --EntityState:Authority=Events > events.json
dotnet run --project eng/examples/aspire-first/AppHost -- explain -- \
  --EntityState:Authority=Events --OrderReads:Source=Materialized \
  --OrderReads:AcceptEventualConsistency=true > eventual.json
```

In report schema `cohesive.application.explanation/2`, follow:

- `endpoints[].service`: exact service reference and original selected operation. The fulfillment operation
  declares `Durable` lifetime and `Admission` completion independently; the result operation has its own authorization.
- `services[]`: the original canonical service documents, preserving their fingerprints. These supplement
  `executions[]`, which retains the process documents; no new system fingerprint is introduced.
- `history[]`: the selected state authority and reconstitution policy.
- `storage.queries[].read`: serving backend and consistency. Only selected order details become `Eventual`;
  availability and entity operations retain their existing bindings.
- `infrastructure` / `deployment`: existing admitted native associations.

This is **offline admission**, not a native health or freshness report. It does not open the comparison file,
resolve clients or assert that the scheduler is deployed. The local scheduler remains an ordinary Aspire resource;
no portable infrastructure durability witness is inferred from its image name.

## Try the interactive application

Run the [AppHost](README.md#declare--attach--bind--run), create an order and follow the
[fulfillment walkthrough](FULFILLMENT.md#local-walkthrough). Reuse the exact command/idempotency/instance/attempt
and input only when retrying the same intent. State-mode SQL seeding is described there; the automated proof
uses the configured repository to seed both authority modes without bypassing event history.

Use the [materialization command](OrderMaterialization/README.md) against that same database and authority:
initialize explicitly, rebuild an attempt, catch up after fulfillment, and save a successful comparison.
Only then select `OrderReads__Source=Materialized` with explicit eventual consent and the comparison file.
Restarting with `OrderReads__Source=Live` rolls back reads without deleting the generation or resetting CDC.
Keep catch-up running as bounded operator activations while selected hosts use that generation.

Every worker sharing a task hub must bind the same application deployment and storage authority; use separate hubs for independently configured deployments.

The demo qualifies worker replacement while the scheduler is available. Scheduler-container loss, production
recovery/retention policies, authentication, and automatic compensation for arbitrary infrastructure failures
remain separate work. Live feature-flag adapters and fleet-wide cutover are also outside this slice.

## Live flag selection after qualification

Once the comparison artifact and native readers are qualified, configure the worker:

```json
{
  "OrderReads": {
    "Source": "FeatureFlag",
    "FeatureFlag": "orders-materialized",
    "AcceptEventualConsistency": true,
    "ComparisonFile": "/absolute/path/to/comparison.json"
  },
  "FeatureManagement": { "orders-materialized": false }
}
```

Retain the existing `Shadow` target, slot-generation and position-key configuration.
The storage model prepares both backends before serving traffic. The worker uses
Microsoft feature management; editing a reload-enabled worker appsettings file from
false to true selects Elasticsearch on subsequent reads. Set it false to select
live PostgreSQL without restarting. In-flight reads keep their original backend.
The comparison/generation is admitted at startup; rotating a generation still
requires explicit admission/rebinding. This does not turn off CDC or materialization.

With Aspire, `OrderReads` authoring is forwarded to the worker, while live flag values
must come from the worker's reloadable provider. Environment variables supplied by
AppHost are startup configuration, not a live flag transport. For production use
[Azure App Configuration](../../../src/adapters/Cohesive.Adapters.FeatureManagement/README.md)
or [LaunchDarkly](../../../src/adapters/Cohesive.Adapters.LaunchDarkly/README.md)
with host-owned clients and refresh. Neither cloud service is required by the local proof.

The explanation includes both candidates and the flag identity. It does not claim a
current decision or synchronized fleet cutover. Selected-backend errors propagate;
rollback is an operator flag decision, never a hidden fallback. Feature targeting
is separate from request authorization, and asynchronous visibility remains explicit.

`bash eng/test-fulfillment-composition.sh` now also flips false → true → false in
one running HTTP host under both state and event authority. Provider tests exercise
native configuration refresh and contextual targeting; they do not contact hosted
Azure or LaunchDarkly accounts.
