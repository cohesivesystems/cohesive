using System.Collections.Immutable;
using Cohesive.Adapters.DurableTask;
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

public sealed class ProcessNativeEntityInteractionTests
{
    [Fact]
    public async Task AuthoredProcess_ReadEvaluateComputeAndTransition_ExecutesEndToEnd()
    {
        var storage = new InMemoryProcessStorageAdapter();
        var checkpoints = new RoundTrippingCheckpointRepository(storage);
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

        var relationRuntime = CustomerProfileRelationRuntime.Create(
            segments:
            [
                new("segment-enterprise", "enterprise")
            ],
            orders:
            [
                new("order-1", "customer-1", 42.5m),
                new("order-2", "customer-1", 99.0m)
            ]);
        var evaluator = relationRuntime.Evaluator;

        IProcessEngine engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost().Register(CustomerRecordEntity.Define()),
            entityRepository: storage,
            checkpointRepository: checkpoints,
            relationQueryEvaluator: evaluator,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()
            )
        );

        using var cancellation = new CancellationTokenSource();
        var run = await engine.ExecuteAsync(
            OperationContext.Create(cancellationToken: cancellation.Token),
            new CustomerProjectionProcess(),
            "customer-1"
            );

        Assert.Equal("enterprise:alice", run.Result.UpdatedName);
        Assert.Equal("customer-1", run.Result.Profile.CustomerId);
        Assert.Equal("enterprise", run.Result.Profile.Segment.DisplayName);
        Assert.Collection(
            run.Result.Profile.Orders,
            order =>
            {
                Assert.Equal("order-1", order.OrderId);
                Assert.Equal(42.5m, order.Total);
            },
            order =>
            {
                Assert.Equal("order-2", order.OrderId);
                Assert.Equal(99.0m, order.Total);
            });
        Assert.Collection(
            evaluator.Evaluations,
            evaluation =>
            {
                Assert.Equal("customer-segment", Assert.IsType<RelationDefinition>(evaluation.Definition).Id.Value);
                Assert.Equal("process/customer/customer-1/segment", evaluation.Evaluation.Value);
            },
            evaluation =>
            {
                Assert.Equal("customer-orders", Assert.IsType<RelationDefinition>(evaluation.Definition).Id.Value);
                Assert.Equal("process/customer/customer-1/orders", evaluation.Evaluation.Value);
            });
        Assert.Equal(cancellation.Token, evaluator.LastCancellationToken);
        Assert.All(evaluator.Evaluations, evaluation =>
        {
            var suppliedRoot = Assert.Single(evaluation.SuppliedRoots!.Observations);
            Assert.Equal("customer-1", suppliedRoot.Id);
            Assert.Equal(
                ObservationValue.FromString("segment-enterprise"),
                suppliedRoot.Fields[nameof(CustomerReadModel.SegmentId)]);
        });
        Assert.Single(relationRuntime.SegmentReader.Requests);
        var segmentLookup = Assert.IsType<RelationQueryIdentityBatchLookup>(
            relationRuntime.SegmentReader.Requests[0].Constraint);
        Assert.Equal("segment-enterprise", Assert.Single(segmentLookup.Identities));
        Assert.Single(relationRuntime.OrderReader.Requests);
        var orderLookup = Assert.IsType<RelationQueryRelationshipKeyBatchLookup>(
            relationRuntime.OrderReader.Requests[0].Constraint);
        Assert.Equal("customer-1", Assert.Single(orderLookup.Keys));
        Assert.Equal(FieldPath.FromField(nameof(OrderReadModel.CustomerId)), orderLookup.RelationshipReference);
        var replaySegment = CustomerProfileRelation.CreateSegmentEvaluation(
            new("customer-1", "alice", "segment-enterprise"));
        Assert.Equal(evaluator.Evaluations[0].Evaluation, replaySegment.Evaluation);
        Assert.Equal(
            evaluator.Evaluations[0].Fingerprint,
            replaySegment.Fingerprint);
        Assert.DoesNotContain(
            run.Variables.Values,
            static value => value is RelationQueryEvaluationOutcome);
        var checkpoint = Assert.IsType<ProcessCheckpoint>(await checkpoints.LoadCheckpointAsync(
            OperationContext.Create(),
            run.ProcessId));
        var converter = new DurableTaskSystemTextJsonDataConverter();
        var restoredCheckpoint = Assert.IsType<ProcessCheckpoint>(converter.Deserialize(
            converter.Serialize(checkpoint),
            typeof(ProcessCheckpoint)));
        Assert.IsType<CustomerSegmentRelationRow>(restoredCheckpoint.Variables["segmentEvaluation"]);
        Assert.IsType<CustomerOrderRelationRow[]>(restoredCheckpoint.Variables["orderEvaluation"]);
        Assert.True(checkpoints.RoundTripCount > 2);
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
    public async Task AuthoredProcess_EvaluateWithoutConfiguredEvaluator_ThrowsActionableError()
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
        IProcessEngine engine = new ProcessEngine(new(
            transitionHost: new DeclarativeTransitionHost().Register(CustomerRecordEntity.Define()),
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            OperationContext.Create(),
            new CustomerProjectionProcess(),
            "customer-1"));

        Assert.Contains(nameof(IRelationQueryEvaluator), exception.Message, StringComparison.Ordinal);
        Assert.Contains("evaluation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelationQueryEvaluationNode_RequiresResultProjection()
    {
        var evaluation = CreateSegmentEvaluation();

        Assert.Throws<ArgumentNullException>(() => new ProcessDefinitionBuilder("ProjectionRequired")
            .AddRelationQueryEvaluationNode(
                name: "evaluate",
                evaluationExpression: _ => evaluation,
                resultExpression: null!));
    }

    [Fact]
    public async Task RelationQueryEvaluationNode_NullEvaluatorOutcome_IsRejectedBeforeProjection()
    {
        var projectionCalled = false;
        var evaluator = new DelegateRelationQueryEvaluator(
            static (_, _) => ValueTask.FromResult<RelationQueryEvaluationOutcome>(null!));
        var engine = CreateEvaluationEngine(new InMemoryProcessStorageAdapter(), evaluator);
        var process = CreateEvaluationProcess(
            CreateSegmentEvaluation(),
            _ =>
            {
                projectionCalled = true;
                return "projected";
            });

        var exception = await Assert.ThrowsAsync<SemanticRuleViolationException>(() => engine.ExecuteAsync(
            OperationContext.Create(),
            process));

        Assert.Contains("returned null", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(projectionCalled);
    }

    [Fact]
    public async Task RelationQueryEvaluationNode_ForeignEvaluatorOutcome_IsRejectedBeforeProjection()
    {
        var relationRuntime = CustomerProfileRelationRuntime.Create(
            segments: [new("segment-enterprise", "enterprise")],
            orders: []);
        var foreignEvaluation = CustomerProfileRelation.CreateSegmentEvaluation(
            new("customer-foreign", "foreign", "segment-enterprise"));
        var projectionCalled = false;
        var evaluator = new DelegateRelationQueryEvaluator(
            (_, cancellationToken) => relationRuntime.Evaluator.EvaluateAsync(
                foreignEvaluation,
                cancellationToken));
        var engine = CreateEvaluationEngine(new InMemoryProcessStorageAdapter(), evaluator);
        var process = CreateEvaluationProcess(
            CreateSegmentEvaluation(),
            _ =>
            {
                projectionCalled = true;
                return "projected";
            });

        var exception = await Assert.ThrowsAsync<SemanticRuleViolationException>(() => engine.ExecuteAsync(
            OperationContext.Create(),
            process));

        Assert.Contains("different evaluation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(projectionCalled);
    }

    [Fact]
    public async Task RelationQueryEvaluationNode_ProjectionCannotReturnNonWireOutcome()
    {
        var relationRuntime = CustomerProfileRelationRuntime.Create(
            segments: [new("segment-enterprise", "enterprise")],
            orders: []);
        var engine = CreateEvaluationEngine(
            new InMemoryProcessStorageAdapter(),
            relationRuntime.Evaluator);
        var process = CreateEvaluationProcess(
            CreateSegmentEvaluation(),
            static outcome => outcome);

        var exception = await Assert.ThrowsAsync<SemanticRuleViolationException>(() => engine.ExecuteAsync(
            OperationContext.Create(),
            process));

        Assert.Contains(nameof(RelationQueryEvaluationOutcome), exception.Message, StringComparison.Ordinal);
        Assert.Contains("application-owned checkpoint value", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelationQueryEvaluationNode_PropagatesCancellationToEvaluator()
    {
        var evaluator = new BlockingRelationQueryEvaluator();
        var engine = CreateEvaluationEngine(new InMemoryProcessStorageAdapter(), evaluator);
        var process = CreateEvaluationProcess(
            CreateSegmentEvaluation(),
            static _ => "projected");
        using var cancellation = new CancellationTokenSource();

        var execution = engine.ExecuteAsync(
            OperationContext.Create(cancellationToken: cancellation.Token),
            process);
        await evaluator.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(cancellation.Token, evaluator.CancellationToken);
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

    static RelationQueryEvaluation CreateSegmentEvaluation() =>
        CustomerProfileRelation.CreateSegmentEvaluation(
            new("customer-1", "alice", "segment-enterprise"));

    static ProcessDefinition CreateEvaluationProcess(
        RelationQueryEvaluation evaluation,
        Func<RelationQueryEvaluationOutcome, object?> projectResult) =>
        new ProcessDefinitionBuilder("EvaluateRelation")
            .AddRelationQueryEvaluationNode(
                name: "evaluate",
                evaluationExpression: _ => evaluation,
                resultExpression: projectResult,
                resultVariable: "result",
                nextNode: "end")
            .AddEndNode(
                name: "end",
                resultExpression: context => context.RequireVariable<object>("result"))
            .Build();

    static ProcessEngine CreateEvaluationEngine(
        InMemoryProcessStorageAdapter storage,
        IRelationQueryEvaluator evaluator) =>
        new(new(
            transitionHost: new DeclarativeTransitionHost().Register(CustomerRecordEntity.Define()),
            entityRepository: storage,
            checkpointRepository: storage,
            relationQueryEvaluator: evaluator,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));

}

sealed class RoundTrippingCheckpointRepository(IProcessCheckpointRepository inner)
    : IProcessCheckpointRepository
{
    readonly DurableTaskSystemTextJsonDataConverter converter = new();

    public int RoundTripCount { get; private set; }

    public Task SaveCheckpointAsync(OperationContext context, ProcessCheckpoint checkpoint) =>
        inner.SaveCheckpointAsync(context, RoundTrip(checkpoint));

    public async Task<ProcessCheckpoint?> LoadCheckpointAsync(OperationContext context, string processId)
    {
        var checkpoint = await inner.LoadCheckpointAsync(context, processId);
        return checkpoint is null ? null : RoundTrip(checkpoint);
    }

    ProcessCheckpoint RoundTrip(ProcessCheckpoint checkpoint)
    {
        RoundTripCount++;
        return (ProcessCheckpoint)(converter.Deserialize(
            converter.Serialize(checkpoint),
            typeof(ProcessCheckpoint))
            ?? throw new InvalidOperationException("Checkpoint round trip returned null."));
    }
}

sealed class RecordingRelationQueryEvaluator : IRelationQueryEvaluator
{
    readonly IRelationQueryEvaluator inner;
    readonly List<RelationQueryEvaluation> evaluations = [];

    public RecordingRelationQueryEvaluator(IRelationQueryEvaluator inner) =>
        this.inner = Guard.RequireNotNull(inner);

    public RelationQueryEvaluation? LastEvaluation { get; private set; }

    public IReadOnlyList<RelationQueryEvaluation> Evaluations => evaluations;

    public CancellationToken LastCancellationToken { get; private set; }

    public async ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        LastEvaluation = evaluation;
        evaluations.Add(evaluation);
        LastCancellationToken = cancellationToken;
        return await inner.EvaluateAsync(evaluation, cancellationToken);
    }
}

sealed class DelegateRelationQueryEvaluator(
    Func<RelationQueryEvaluation, CancellationToken, ValueTask<RelationQueryEvaluationOutcome>> evaluate)
    : IRelationQueryEvaluator
{
    readonly Func<RelationQueryEvaluation, CancellationToken, ValueTask<RelationQueryEvaluationOutcome>> evaluate =
        Guard.RequireNotNull(evaluate);

    public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken = default) =>
        evaluate(evaluation, cancellationToken);
}

sealed class BlockingRelationQueryEvaluator : IRelationQueryEvaluator
{
    readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => entered.Task;

    public CancellationToken CancellationToken { get; private set; }

    public async ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        CancellationToken = cancellationToken;
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocking evaluator should only complete through cancellation.");
    }
}

static class CustomerProfileRelation
{
    static readonly RelationQueryExpressionAuthoring Author;
    static readonly RelationQueryAuthoringResult<RelationDefinition> SegmentRelation;
    static readonly RelationQueryAuthoringResult<RelationDefinition> OrderRelation;

    static CustomerProfileRelation()
    {
        Author = RelationQuery.Expression();
        var customerSegment = Author.Relationship<CustomerReadModel, SegmentReadModel>(
            customer => customer.SegmentId,
            new("customer/segment"));
        var orderCustomer = Author.Relationship<OrderReadModel, CustomerReadModel>(
            order => order.CustomerId,
            new("order/customer"));
        var customers = Author.Source<CustomerReadModel>();

        // A single Customer -> Segment -> inverse Orders body is canonical, but physical planning v1 reports
        // REL2106 because the second traversal follows a cardinality-changing traversal. Keep both traversals as
        // independently executable supplied-root relations until that reachability lowering is supported.
        var segments = Author.Traverse(
            customers,
            customerSegment,
            joinKind: JoinKind.Inner,
            requirement: QueryInputRequirement.Required);
        var segmentRows = Author.Project(
            segments.Node,
            (CustomerReadModel customer, SegmentReadModel segment) => new CustomerSegmentRelationRow
            {
                CustomerId = customer.CustomerId,
                SegmentId = segment.SegmentId,
                SegmentDisplayName = segment.DisplayName
            },
            customers.Binding,
            segments.Binding);
        SegmentRelation = Author.BuildRelation(
            customers,
            segmentRows,
            mode: RelationOutputMode.OnePerRoot,
            id: new("customer-segment"),
            name: new("CustomerSegment"));

        var orders = Author.TraverseInverse(
            customers,
            orderCustomer,
            joinKind: JoinKind.Inner,
            requirement: QueryInputRequirement.Required);
        var orderRows = Author.Project(
            orders.Node,
            (CustomerReadModel customer, OrderReadModel order) =>
                new CustomerOrderRelationRow
                {
                    CustomerId = customer.CustomerId,
                    OrderId = order.OrderId,
                    OrderCustomerId = order.CustomerId,
                    OrderTotal = order.Total
                },
            customers.Binding,
            orders.Binding);
        OrderRelation = Author.BuildRelation(
            customers,
            orderRows,
            mode: RelationOutputMode.ManyPerRoot,
            id: new("customer-orders"),
            name: new("CustomerOrders"));

        CustomerShape = customers.Binding.Shape!.Value;
        SegmentShape = segments.Binding.Shape!.Value;
        OrderShape = orders.Binding.Shape!.Value;
    }

    public static QualifiedShapeId CustomerShape { get; }

    public static QualifiedShapeId SegmentShape { get; }

    public static QualifiedShapeId OrderShape { get; }

    public static RelationQueryEvaluation CreateSegmentEvaluation(CustomerReadModel customer) =>
        CreateEvaluation(
            SegmentRelation,
            new($"process/customer/{customer.CustomerId}/segment"),
            customer);

    public static RelationQueryEvaluation CreateOrderEvaluation(CustomerReadModel customer) =>
        CreateEvaluation(
            OrderRelation,
            new($"process/customer/{customer.CustomerId}/orders"),
            customer);

    static RelationQueryEvaluation CreateEvaluation(
        RelationQueryAuthoringResult<RelationDefinition> relation,
        RelationQueryEvaluationId evaluation,
        CustomerReadModel customer) =>
        Author
            .Evaluate(relation, evaluation)
            .Supply(
                [customer],
                static value => value.CustomerId,
                evidenceReference: "process/customer-read")
            .Build();

    public static CustomerSegmentRelationRow ProjectSegment(RelationQueryEvaluationOutcome outcome)
    {
        var segmentRelation = RequireRelation(outcome, "customer-segment");
        if (segmentRelation.Rows.Length != 1)
            throw new InvalidOperationException("Customer-segment relation evaluation must produce exactly one row.");
        var segmentRow = segmentRelation.Rows[0].Value;
        return new()
        {
            CustomerId = RequiredString(segmentRow, nameof(CustomerSegmentRelationRow.CustomerId)),
            SegmentId = RequiredString(segmentRow, nameof(CustomerSegmentRelationRow.SegmentId)),
            SegmentDisplayName = RequiredString(segmentRow, nameof(CustomerSegmentRelationRow.SegmentDisplayName))
        };
    }

    public static CustomerOrderRelationRow[] ProjectOrders(RelationQueryEvaluationOutcome outcome)
    {
        var orderRelation = RequireRelation(outcome, "customer-orders");
        if (orderRelation.Rows.IsDefaultOrEmpty)
            throw new InvalidOperationException("Customer-orders relation evaluation produced no rows.");
        return
        [
            .. orderRelation.Rows
                .Select(static row => row.Value)
                .Select(value => new CustomerOrderRelationRow
                {
                    CustomerId = RequiredString(value, nameof(CustomerOrderRelationRow.CustomerId)),
                    OrderId = RequiredString(value, nameof(CustomerOrderRelationRow.OrderId)),
                    OrderCustomerId = RequiredString(value, nameof(CustomerOrderRelationRow.OrderCustomerId)),
                    OrderTotal = RequiredDecimal(value, nameof(CustomerOrderRelationRow.OrderTotal))
                })
                .OrderBy(static order => order.OrderId, StringComparer.Ordinal)
        ];
    }

    public static CustomerProfile CreateProfile(
        CustomerSegmentRelationRow segmentRow,
        IReadOnlyList<CustomerOrderRelationRow> orderRows)
    {
        ArgumentNullException.ThrowIfNull(segmentRow);
        ArgumentNullException.ThrowIfNull(orderRows);
        var customerId = segmentRow.CustomerId;
        var segment = new SegmentReadModel(
            segmentRow.SegmentId,
            segmentRow.SegmentDisplayName);
        var orders = orderRows
            .Select(static value => new OrderReadModel(
                value.OrderId,
                value.OrderCustomerId,
                value.OrderTotal))
            .OrderBy(static order => order.OrderId, StringComparer.Ordinal)
            .ToArray();

        if (orders.Any(order => !string.Equals(order.CustomerId, customerId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Customer-profile relation returned an order for another customer.");

        return new(customerId, segment, orders);
    }

    static RelationQueryRelationResult RequireRelation(
        RelationQueryEvaluationOutcome outcome,
        string name)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status != RelationQueryExecutionStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"{name} relation evaluation did not succeed ({outcome.Status}): "
                + string.Join(
                    "; ",
                    outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic =>
                        $"execution {diagnostic.Code}: {diagnostic.Message}")
                    ?? outcome.PhysicalPlanning?.Diagnostics.Select(static diagnostic =>
                        $"planning {diagnostic.Code}: {diagnostic.Message}")
                    ?? outcome.Realization?.Diagnostics.Select(static diagnostic =>
                        $"realization {diagnostic.Code}: {diagnostic.Message}")
                    ?? outcome.Compilation.Diagnostics.Select(static diagnostic =>
                        $"compilation {diagnostic.Code}: {diagnostic.Message}")));
        }

        return outcome.Result?.Relation
            ?? throw new InvalidOperationException($"{name} relation evaluation produced no relation result.");
    }

    static string RequiredString(ObservationValue value, string property) =>
        value.GetProperty(property).String
        ?? throw new InvalidOperationException($"Customer-profile field '{property}' is not a string.");

    static decimal RequiredDecimal(ObservationValue value, string property)
    {
        var scalar = value.GetProperty(property);
        return scalar.Kind switch
        {
            ObservationValueKind.Decimal => scalar.Decimal,
            ObservationValueKind.Int64 => scalar.Int64,
            _ => throw new InvalidOperationException($"Customer-profile field '{property}' is not numeric.")
        };
    }
}

sealed class CustomerSegmentRelationRow
{
    public string CustomerId { get; init; } = string.Empty;

    public string SegmentId { get; init; } = string.Empty;

    public string SegmentDisplayName { get; init; } = string.Empty;
}

sealed class CustomerOrderRelationRow
{
    public string CustomerId { get; init; } = string.Empty;

    public string OrderId { get; init; } = string.Empty;

    public string OrderCustomerId { get; init; } = string.Empty;

    public decimal OrderTotal { get; init; }
}

sealed record CustomerProfileRelationRuntime(
    RecordingRelationQueryEvaluator Evaluator,
    CustomerProfileSourceReader SegmentReader,
    CustomerProfileSourceReader OrderReader)
{
    static readonly RelationQuerySourceInstanceId CustomerSource = new("tests/process/customers");
    static readonly RelationQuerySourceInstanceId SegmentSource = new("tests/process/segments");
    static readonly RelationQuerySourceInstanceId OrderSource = new("tests/process/orders");

    public static CustomerProfileRelationRuntime Create(
        ImmutableArray<SegmentReadModel> segments,
        ImmutableArray<OrderReadModel> orders)
    {
        var segmentReader = new CustomerProfileSourceReader(
            Descriptor(SegmentSource),
            [
                .. segments.Select(segment => CustomerProfileSourceRow.Create(
                    segment.SegmentId,
                    (nameof(SegmentReadModel.SegmentId), ObservationValue.FromString(segment.SegmentId)),
                    (nameof(SegmentReadModel.DisplayName), ObservationValue.FromString(segment.DisplayName))))
            ]);
        var orderReader = new CustomerProfileSourceReader(
            Descriptor(OrderSource),
            [
                .. orders.Select(order => CustomerProfileSourceRow.Create(
                    order.OrderId,
                    (nameof(OrderReadModel.OrderId), ObservationValue.FromString(order.OrderId)),
                    (nameof(OrderReadModel.CustomerId), ObservationValue.FromString(order.CustomerId)),
                    (nameof(OrderReadModel.Total), ObservationValue.FromDecimal(order.Total))))
            ]);
        RelationQueryEvaluator evaluator = new(
            CreatePlacement,
            new(
                new("tests/process/customer-profile/v1"),
                conventionSetVersion: "tests/process/customer-profile-conventions/v1",
                maximumBatchSize: 16,
                maximumBufferedRows: 100,
                maximumLocalRows: 100,
                maximumFanOut: 100,
                maximumReferenceKeysPerObservation: 16,
                maximumConcurrency: 2),
            [segmentReader, orderReader]);
        return new(new(evaluator), segmentReader, orderReader);
    }

    static RelationQuerySourcePlacement CreatePlacement(CompiledRelationQueryPlan plan)
    {
        List<RelationQuerySourcePlacementBinding> bindings = [];
        foreach (var source in plan.InputContract.Sources)
        {
            bindings.Add(new(
                new($"placement/{Uri.EscapeDataString(source.Input.Id.Value)}"),
                source.Input.Id,
                source.Node,
                source.Binding,
                source.Shape,
                SourceFor(source.Shape),
                RelationQuerySourcePlacementBindingKind.SourceSet,
                source.Role == RelationQuerySourceInputRole.RelationRoot
                    ? RelationQuerySourceAcquisitionKind.Supplied
                    : RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                RelationQuerySourcePlacementOrigin.Explicit,
                identity: source.Role == RelationQuerySourceInputRole.RelationRoot
                    ? null
                    : new(source.Shape, "$identity"),
                fields: FieldBindings(source.Fields)));
        }

        foreach (var traversal in plan.InputContract.Traversals)
        {
            bindings.Add(new(
                new($"placement/{Uri.EscapeDataString(traversal.Input.Id.Value)}"),
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                SourceFor(traversal.ResultShape),
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, "$identity"),
                FieldBindings(traversal.Fields),
                traversal.Input.Direction == RelationshipTraversalDirection.Inverse
                    ? [new(traversal.Input.Id, traversal.Definition.SourceReference, "$relationship")]
                    : []));
        }

        var sources = bindings
            .Select(static binding => binding.Source)
            .Distinct()
            .Select(source => new RelationQuerySourceInstance(
                source,
                Domain(source),
                Profile(source),
                new(
                    maximumBatchSize: 16,
                    maximumBufferedRows: 100,
                    maximumFanOut: 100,
                    maximumConcurrency: 2)))
            .ToImmutableArray();
        return new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            conventionSetVersion: "tests/process/customer-profile-placement/v1",
            sources,
            [.. bindings]);
    }

    static ImmutableArray<RelationQuerySourceFieldBinding> FieldBindings(
        ImmutableArray<RelationQueryFieldInputContract> fields) =>
    [
        .. fields.Select(static field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            $"field/{Uri.EscapeDataString(field.Input.Id.Value)}"))
    ];

    static RelationQuerySourceReaderDescriptor Descriptor(RelationQuerySourceInstanceId source) =>
        new(source, Domain(source), Profile(source));

    static RelationQueryExecutionDomainId Domain(RelationQuerySourceInstanceId source) =>
        new($"domain/{source.Value}");

    static RelationQueryTargetCapabilityProfile Profile(RelationQuerySourceInstanceId source)
    {
        RelationQueryPrimitiveCapabilityKind[] capabilities =
        [
            RelationQueryPrimitiveCapabilityKind.KeyExtraction,
            RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            RelationQueryPrimitiveCapabilityKind.PredicateRead,
            RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.LocalCorrelation,
            RelationQueryPrimitiveCapabilityKind.HashJoin,
            RelationQueryPrimitiveCapabilityKind.FieldProjection,
            RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead,
            RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead,
            RelationQueryPrimitiveCapabilityKind.ProvenanceTracking,
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup
        ];
        return new(
            new($"target/{source.Value}"),
            new($"target/{source.Value}/v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                .. capabilities.Select(capability => new RelationQueryTargetCapabilityEvidence(
                    new($"evidence/{(int)capability}"),
                    new PrimitiveRelationQueryCapability(capability)))
            ]);
    }

    static RelationQuerySourceInstanceId SourceFor(QualifiedShapeId shape) =>
        shape == CustomerProfileRelation.CustomerShape
            ? CustomerSource
            : shape == CustomerProfileRelation.SegmentShape
                ? SegmentSource
                : shape == CustomerProfileRelation.OrderShape
                    ? OrderSource
                    : throw new InvalidOperationException($"No process test source is configured for '{shape}'.");
}

sealed class CustomerProfileSourceReader : IRelationQuerySourceReader
{
    readonly ImmutableArray<CustomerProfileSourceRow> rows;
    readonly List<RelationQuerySourceReadRequest> requests = [];

    public CustomerProfileSourceReader(
        RelationQuerySourceReaderDescriptor descriptor,
        ImmutableArray<CustomerProfileSourceRow> rows)
    {
        Descriptor = descriptor;
        this.rows = rows;
    }

    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    public IReadOnlyList<RelationQuerySourceReadRequest> Requests => requests;

    public ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        requests.Add(request);
        var selected = request.Constraint switch
        {
            RelationQueryIdentityBatchLookup lookup =>
                rows.Where(row => lookup.Identities.Contains(row.Identity, StringComparer.Ordinal)),
            RelationQueryRelationshipKeyBatchLookup lookup =>
                rows.Where(row => row.Fields.TryGetValue(lookup.RelationshipReference, out var value)
                    && value.String is { } key
                    && lookup.Keys.Contains(key, StringComparer.Ordinal)),
            _ => throw new NotSupportedException(
                $"The process test reader does not support '{request.Constraint.GetType().Name}'.")
        };
        var observations = selected.Select(row => new RelationQuerySourceReadObservation(
            row.Identity,
            request.Shape,
            [
                .. request.Fields.Select(field => row.Fields.TryGetValue(field.SemanticPath, out var value)
                    ? new RelationQuerySourceReadFieldResult(
                        field,
                        RelationQuerySourceReadFieldState.Value,
                        value)
                    : new RelationQuerySourceReadFieldResult(
                        field,
                        RelationQuerySourceReadFieldState.Missing,
                        evidenceReference: $"tests/process/missing/{field.SemanticPath}"))
            ])).ToImmutableArray();
        return ValueTask.FromResult(new RelationQuerySourceReadResult(
            RelationQuerySourceReadState.Complete,
            observations,
            $"tests/process/{request.Stage.Value}"));
    }
}

sealed record CustomerProfileSourceRow(
    string Identity,
    ImmutableDictionary<FieldPath, ObservationValue> Fields)
{
    public static CustomerProfileSourceRow Create(
        string identity,
        params (string Field, ObservationValue Value)[] fields) =>
        new(
            identity,
            fields.ToImmutableDictionary(
                static field => FieldPath.FromField(field.Field),
                static field => field.Value));
}

[GenerateProcessDefinition(nameof(Build))]
public partial class CustomerProjectionProcess : IProcessDefinition<string, CustomerProjectionResult>
{
    static readonly CustomerRecordEntity CustomerRecordEntity = CustomerRecordEntity.Instance;

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

        var segmentEvaluation = await process.Evaluate(
            CustomerProfileRelation.CreateSegmentEvaluation(customer),
            outcome => CustomerProfileRelation.ProjectSegment(outcome));
        var orderEvaluation = await process.Evaluate(
            CustomerProfileRelation.CreateOrderEvaluation(customer),
            outcome => CustomerProfileRelation.ProjectOrders(outcome));
        var profile = await process.Compute(CustomerProfileRelation.CreateProfile(segmentEvaluation, orderEvaluation));
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
