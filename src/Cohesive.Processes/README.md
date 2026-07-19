# Cohesive.Processes

Declarative process definitions and runtime infrastructure for multistep workflows over entities, canonical relations and queries, waits, signals, and effects.

## Install

```bash
dotnet add package Cohesive.Processes
```

## Use When

- You need process definitions that coordinate entity transitions, entity reads, canonical relation/query evaluations, waits, requests, and effects.
- You want deterministic process planning and runtime state that can be interpreted by different storage or orchestration adapters.
- You need a semantic process model separate from a specific queue, workflow engine, or database.

## Example

```csharp
using Cohesive.Processes.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

[GenerateProcessDefinition(nameof(Build))]
public partial class DispatchCustomerProcess : IProcessDefinition<string, DispatchCustomerResult>
{
    static readonly CustomerEntity Customers = CustomerEntity.Instance;
    static readonly CarrierEntity Carriers = CarrierEntity.Instance;

    async ProcessTask<DispatchCustomerResult> Build(
        ProcessAuthoringContext<string, DispatchCustomerResult> process,
        string customerId)
    {
        var customer = await process.Read(Customers.ReadById(customerId, snapshot =>
            new CustomerReadModel(
                CustomerId: snapshot.Require(entity => entity.Id),
                Name: snapshot.Require(entity => entity.Name),
                SegmentId: snapshot.Require(entity => entity.SegmentId))));

        // CustomerProfiles is a persisted canonical relation/query document plus its
        // shape and relationship snapshots. The helper authors one exact evaluation.
        var evaluation = CustomerProfiles.ForCustomer(
            customerId,
            evaluationId: $"dispatch/{customerId}/customer-profile");
        var profile = await process.Evaluate(
            evaluation,
            outcome => CustomerProfiles.RequireSingleProfile(outcome));
        var reservation = await process.Request(new ReserveCarrierRequest(
            CustomerId: profile.CustomerId,
            OrderCount: profile.Orders.Count));

        var customerUpdate = await process.Transition(
            Customers.MarkDispatched,
            entityId: profile.CustomerId,
            input: new(reservation.CarrierId));

        var carrierUpdate = await process.Transition(
            Carriers.ReserveCapacity,
            entityId: reservation.CarrierId,
            input: new(reservation.ReservedOrderCount));

        return process.Return(new(
            CustomerId: profile.CustomerId,
            CarrierId: reservation.CarrierId,
            CustomerVersion: customerUpdate.NewVersion,
            CarrierVersion: carrierUpdate.NewVersion));
    }
}

public sealed record ReserveCarrierRequest(string CustomerId, int OrderCount)
    : IEffectRequest<CarrierReservation>
{
    public static string RequestName => "ReserveCarrier";
}

public sealed record CarrierReservation(string CarrierId, int ReservedOrderCount);

public sealed record DispatchCustomerResult(
    string CustomerId,
    string CarrierId,
    long CustomerVersion,
    long CarrierVersion);
```

Configure one `IRelationQueryEvaluator` on `ProcessRuntimeServices`. The same evaluator boundary compiles,
realizes, physically plans, acquires, and interprets both canonical relation and query definitions; process code
does not select repositories or execution engines directly. Evaluation identifiers should be deterministic from
the process instance and semantic operation so replay produces the same attribution.

`RelationQueryEvaluationOutcome` deliberately retains in-process compiler, placement, acquisition, and execution
artifacts rather than defining a durable wire schema. Process authoring therefore requires a projection that runs in
the evaluation node before checkpoint capture; only the returned application value can become a process variable.
The runtime rejects a projection that returns the non-wire outcome itself. Evaluation descriptors are portable,
versioned, and fingerprinted; derive their evaluation identifiers deterministically from process and operation
identity so retries and replay retain the same attribution.

## Related Packages

- `Cohesive.Transitions` for entity transition semantics.
- `Cohesive.Storage` for repository adapters.
- `Cohesive.Adapters.DurableTask` for Azure Durable Task execution.
