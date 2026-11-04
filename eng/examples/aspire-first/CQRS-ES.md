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
