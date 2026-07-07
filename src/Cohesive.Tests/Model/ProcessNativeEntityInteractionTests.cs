using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Cohesive.Relations.Queries;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

public sealed class ProcessNativeEntityInteractionTests
{
    [Fact]
    public async Task AuthoredProcess_ReadQueryComputeAndTransition_ExecutesEndToEnd()
    {
        var storage = new InMemoryProcessStorageAdapter();
        SeedEntity(
            storage,
            CustomerRecordEntity.Instance,
            entityId: "customer-1",
            state: new
            {
                Id = "customer-1",
                SegmentId = "segment-enterprise",
                Name = "alice"
            });

        var queryRepositories = new DispatchingReadRepositoryRegistry()
            .Register(
                CustomerProjectionProcess.SegmentSource,
                InMemoryReadRepository.From<SegmentReadModel>(
                    [
                        new(SegmentId: "segment-enterprise", DisplayName: "enterprise")
                    ],
                    idSelector: static segment => segment.SegmentId))
            .Register(
                CustomerProjectionProcess.OrderSource,
                InMemoryReadRepository.From<OrderReadModel>(
                    [
                        new(
                            OrderId: "order-1",
                            CustomerId: "customer-1",
                            Total: 42.5m),
                        new(
                            OrderId: "order-2",
                            CustomerId: "customer-1",
                            Total: 99.0m)
                    ],
                    idSelector: static order => order.OrderId));

        IProcessEngine engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost().Register(CustomerRecordEntity.Define()),
            entityRepository: storage,
            checkpointRepository: storage,
            entityReadRepositoryRegistry: queryRepositories,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()
            )
        );

        var run = await engine.ExecuteAsync(
            OperationContext.Create(),
            new CustomerProjectionProcess(),
            "customer-1"
            );

        Assert.Equal("enterprise:alice", run.Result.UpdatedName);
        Assert.Equal("customer-1", run.Result.Profile.CustomerId);
        Assert.Equal("enterprise", run.Result.Profile.Segment.DisplayName);
        Assert.Equal(2, run.Result.Profile.Orders.Count);
        Assert.Single(run.Transitions);
        Assert.Empty(run.ExecutedEffects);

        var updated = await storage.Get(
            OperationContext.Create(),
            new(CustomerRecordEntity.Instance.Definition.Name.Value, "customer-1")
            );

        var updatedSnapshot = CustomerRecordEntity.Instance.Snapshot(updated.State);
        Assert.Equal("enterprise:alice", updatedSnapshot.Require(customer => customer.Name));
        Assert.Equal(run.Result.NewVersion, updated.Version);
    }

    [Fact]
    public async Task EntityTransitionNode_AppliesBatchTransitions()
    {
        var storage = new InMemoryProcessStorageAdapter();
        SeedEntity(storage, CounterEntity.Instance, "counter-1", new { Id = "counter-1", Value = 2 });
        SeedEntity(storage, CounterEntity.Instance, "counter-2", new { Id = "counter-2", Value = 5 });

        var engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost().Register(CounterEntity.Define()),
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink())
        );

        var process = new ProcessDefinitionBuilder("BatchIncrement")
            .AddEntityTransitionNode(
                name: "apply",
                transitionExpression: _ => ProcessEntityTransition.Batch(
                [
                    ProcessEntityTransition.For(CounterEntity.Instance.Increment, "counter-1", new(3)),
                    ProcessEntityTransition.For(CounterEntity.Instance.Increment, "counter-2", new(4))
                ]),
                resultVariable: "results",
                nextNode: "end"
                )
            .AddEndNode(
                name: "end",
                resultExpression: ctx => ctx.RequireVariable<IReadOnlyList<TransitionResult>>("results").Count
                )
            .Build();

        var run = await engine.ExecuteAsync(OperationContext.Create(), process);

        Assert.Equal(2, run.Result);
        Assert.Equal(2, run.Transitions.Count);

        var first = CounterEntity.Instance.Snapshot((await storage.Get(
            OperationContext.Create(),
            new(CounterEntity.Instance.Definition.Name.Value, "counter-1"))).State
            );
        var second = CounterEntity.Instance.Snapshot((await storage.Get(
            OperationContext.Create(),
            new(CounterEntity.Instance.Definition.Name.Value, "counter-2"))).State
            );
        Assert.Equal(5, first.Require(counter => counter.Value));
        Assert.Equal(9, second.Require(counter => counter.Value));
    }

    static void SeedEntity<TEntity>(
        InMemoryProcessStorageAdapter storage,
        TEntity entity,
        string entityId,
        object state,
        long version = 0
        ) where TEntity : Entity
    {
        storage.SeedEntity(
            new(entity.Definition.Name.Value, entityId),
            entity.CreateState(entityId, state, version),
            version: version
            );
    }

}

[GenerateProcessDefinition(nameof(Build))]
public partial class CustomerProjectionProcess : IProcessDefinition<string, CustomerProjectionResult>
{
    static readonly CustomerRecordEntity CustomerRecordEntity = CustomerRecordEntity.Instance;
    internal static readonly QuerySource SegmentSource = QuerySource.For<SegmentReadModel>("segments");
    internal static readonly QuerySource OrderSource = QuerySource.For<OrderReadModel>("orders");
    
    async ProcessTask<CustomerProjectionResult> Build(ProcessAuthoringContext<string, CustomerProjectionResult> process, string customerId)
    {
        var customer = await process.Read(CustomerRecordEntity.ReadById(customerId,
            snapshot => new CustomerReadModel(
                CustomerId: snapshot.Require(entity => entity.Id),
                Name: snapshot.Require(entity => entity.Name),
                SegmentId: snapshot.Require(entity => entity.SegmentId)
                )
            )
        );

        var profiles = await process.Query(Query
            .From<CustomerReadModel>([customer], rootId: static root => root.CustomerId)
            .JoinOne<CustomerReadModel, string>(
                alias: "segment",
                source: SegmentSource,
                rootKeySelector: root => root.SegmentId
            )
            .JoinMany<CustomerReadModel, OrderReadModel, string>(
                alias: "orders",
                source: OrderSource,
                rootKey: root => root.CustomerId,
                foreignKey: order => order.CustomerId
            )
            .Select(ctx => new CustomerProfile(
                CustomerId: ctx.RootAs<CustomerReadModel>().CustomerId,
                Segment: ctx.RequireOne<SegmentReadModel>("segment"),
                Orders: ctx.Many<OrderReadModel>("orders")
            )));

        var profile = await process.Compute(profiles[0]);
        var updatedName = await process.Compute(profile.Segment.DisplayName + ":" + customer.Name);
        var rename = await process.Transition(ProcessEntityTransition.For(CustomerRecordEntity.Rename,
            customer.CustomerId, new(updatedName))
        );

        return process.Return(new(profile, updatedName, rename.NewVersion));
    }
}

[GenerateProcessDefinition(nameof(Build))]
public partial class CounterBatchProcess : IProcessDefinition<string, int>
{
    async ProcessTask<int> Build(ProcessAuthoringContext<string, int> process, string entityId)
    {
        var results = await process.TransitionMany(ProcessEntityTransition.Batch(
        [
            ProcessEntityTransition.For(CounterEntity.Instance.Increment, entityId, new(1))
        ]));
        return process.Return(results.Count);
    }
}

public sealed class CustomerRecordEntity : Entity<CustomerRecordEntity>
{
    public sealed record RenameInput(string Name);

    public CustomerRecordEntity()
    {
        Id = WriteOnceField<string>(nameof(Id));
        SegmentId = MutableField<string>(nameof(SegmentId));
        Name = MutableField<string>(nameof(Name));
        Rename = Transition<RenameInput>(nameof(Rename), t => t.Set(customer => customer.Name, (_, input) => input.Name));
    }

    public Field<string> Id { get; }

    public Field<string> SegmentId { get; }

    public Field<string> Name { get; }

    public Transition<CustomerRecordEntity, RenameInput> Rename { get; }
}

public sealed class SegmentEntity : Entity<SegmentEntity>
{
    public SegmentEntity()
    {
        Id = WriteOnceField<string>(nameof(Id));
        DisplayName = MutableField<string>(nameof(DisplayName));
    }

    public Field<string> Id { get; }

    public Field<string> DisplayName { get; }
}

public sealed class OrderEntity : Entity<OrderEntity>
{
    public OrderEntity()
    {
        Id = WriteOnceField<string>(nameof(Id));
        CustomerId = MutableField<string>(nameof(CustomerId));
        Total = MutableField<decimal>(nameof(Total));
    }

    public Field<string> Id { get; }

    public Field<string> CustomerId { get; }

    public Field<decimal> Total { get; }
}

public sealed class CounterEntity : Entity<CounterEntity>
{
    public sealed record IncrementInput(int Delta);

    public CounterEntity()
    {
        Id = WriteOnceField<string>(nameof(Id));
        Value = MutableField<int>(nameof(Value));
        Increment = Transition<IncrementInput>(nameof(Increment), transition => transition.Set(counter => counter.Value, (counter, input) => counter.Value + input.Delta));
    }

    public Field<string> Id { get; }

    public Field<int> Value { get; }

    public Transition<CounterEntity, IncrementInput> Increment { get; }
}

public sealed record CustomerReadModel(
    string CustomerId,
    string Name,
    string SegmentId
);

public sealed record SegmentReadModel(
    string SegmentId,
    string DisplayName
);

public sealed record OrderReadModel(
    string OrderId,
    string CustomerId,
    decimal Total
);

public sealed record CustomerProfile(
    string CustomerId,
    SegmentReadModel Segment,
    IReadOnlyList<OrderReadModel> Orders
);

public sealed record CustomerProjectionResult(
    CustomerProfile Profile,
    string UpdatedName,
    long NewVersion
);
