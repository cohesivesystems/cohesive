# Cohesive.Processes

Declarative process definitions and runtime infrastructure for multistep workflows over entities, queries, waits, signals, and effects.

## Install

```bash
dotnet add package Cohesive.Processes
```

## Use When

- You need process definitions that coordinate entity transitions, repository reads, waits, requests, and effects.
- You want deterministic process planning and runtime state that can be interpreted by different storage or orchestration adapters.
- You need a semantic process model separate from a specific queue, workflow engine, or database.

## Example

```csharp
using Cohesive.Processes.Model;
using Cohesive.Relations.Queries;

[GenerateProcessDefinition(nameof(Build))]
public partial class DispatchCustomerProcess : IProcessDefinition<string, DispatchCustomerResult>
{
    static readonly CustomerEntity Customers = CustomerEntity.Instance;
    static readonly CarrierEntity Carriers = CarrierEntity.Instance;
    static readonly QuerySource SegmentSource = QuerySource.For<SegmentReadModel>("segments");
    static readonly QuerySource OrderSource = QuerySource.For<OrderReadModel>("orders");

    async ProcessTask<DispatchCustomerResult> Build(
        ProcessAuthoringContext<string, DispatchCustomerResult> process,
        string customerId)
    {
        var customer = await process.Read(Customers.ReadById(customerId, snapshot =>
            new CustomerReadModel(
                CustomerId: snapshot.Require(entity => entity.Id),
                Name: snapshot.Require(entity => entity.Name),
                SegmentId: snapshot.Require(entity => entity.SegmentId))));

        var profiles = await process.Query(Query
            .From<CustomerReadModel>([customer], rootId: static root => root.CustomerId)
            .JoinOne<CustomerReadModel, string>(
                alias: "segment",
                source: SegmentSource,
                rootKeySelector: static root => root.SegmentId)
            .JoinMany<CustomerReadModel, OrderReadModel, string>(
                alias: "orders",
                source: OrderSource,
                rootKey: static root => root.CustomerId,
                foreignKey: static order => order.CustomerId)
            .Select(static join => new CustomerProfile(
                CustomerId: join.RootAs<CustomerReadModel>().CustomerId,
                Segment: join.RequireOne<SegmentReadModel>("segment"),
                Orders: join.Many<OrderReadModel>("orders"))));

        var profile = profiles[0];
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

## Related Packages

- `Cohesive.Transitions` for entity transition semantics.
- `Cohesive.Storage` for repository adapters.
- `Cohesive.Adapters.DurableTask` for Azure Durable Task execution.
