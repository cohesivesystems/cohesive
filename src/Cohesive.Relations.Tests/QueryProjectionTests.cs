namespace Cohesive.Relations.Tests;

public sealed class QueryProjectionTests
{
    static readonly QuerySource CustomerSource = QuerySource.For<QueriedCustomerRecord>("customers");
    static readonly QuerySource SegmentSource = QuerySource.For<SegmentRecord>("segments");
    static readonly QuerySource OrderSource = QuerySource.For<OrderRecord>("orders");
    static readonly QuerySource FilteredOrderSource = QuerySource.For<OrderQueryRecord>("filtered-orders");
    static readonly QuerySource BoundOrderSource = QuerySource.For<PointReadOrderRecord>("bound-orders");

    [Fact]
    public async Task EntityQuery_Project_HydratesJoinedSources()
    {
        var repositories = new DispatchingReadRepositoryRegistry()
            .Register(SegmentSource,
                InMemoryReadRepository.From<SegmentRecord>(
                    [new("segment-enterprise", "enterprise")],
                    idSelector: static segment => segment.SegmentId)
                )
            .Register(OrderSource,
                InMemoryReadRepository.From<OrderRecord>(
                    [
                        new("order-1", "customer-1", 42.5m),
                        new("order-2", "customer-1", 99.0m)
                    ],
                    idSelector: static order => order.OrderId)
                );

        var query = Query.From<CustomerRecord>(
                [new("customer-1", "segment-enterprise")],
                rootId: static customer => customer.CustomerId
            )
            .JoinOne<CustomerRecord, string>(
                alias: "segment",
                source: SegmentSource,
                rootKeySelector: static customer => customer.SegmentId
                )
            .JoinMany<CustomerRecord, OrderRecord, string>(
                alias: "orders",
                source: OrderSource,
                rootKey: static customer => customer.CustomerId,
                foreignKey: static order => order.CustomerId
                )
            .Select(ctx => new CustomerProjection(
                CustomerId: ctx.RootAs<CustomerRecord>().CustomerId,
                Segment: ctx.RequireOne<SegmentRecord>("segment"),
                Orders: ctx.Many<OrderRecord>("orders")
                )
            );

        var result = await query.ExecuteAsync(OperationContext.Create(), repositories);
        var projection = Assert.Single(result);
        Assert.Equal("customer-1", projection.CustomerId);
        Assert.Equal("enterprise", projection.Segment.DisplayName);
        Assert.Equal(2, projection.Orders.Count);
    }

    [Fact]
    public void QueryCapabilityInspector_ReportsRequiredCapabilities()
    {
        var query = new EntityPredicate(
            Predicate: new And<FieldPredicate>(
            [
                new FieldPredicate(
                    FieldPath.FromField("status"),
                    new And<ValuePredicate>(
                    [
                        new ExactValuePredicate("open"),
                        new Not<ValuePredicate>(new ExistsValuePredicate())
                    ])),
                new FieldPredicate(
                    FieldPath.FromField("score"),
                    new Or<ValuePredicate>(
                    [
                        NumberRangeValuePredicate.GreaterThanOrEqual(10),
                        new InValuePredicate([1, 2, 3])
                    ]))
            ]),
            Scope: FieldPath.FromField("items"));

        var required = QueryCapabilityInspector.GetRequiredCapabilities(query);

        Assert.True(required.Supports(QueryCapability.Equality));
        Assert.True(required.Supports(QueryCapability.Exists));
        Assert.True(required.Supports(QueryCapability.NumberRange));
        Assert.True(required.Supports(QueryCapability.SetMembership));
        Assert.True(required.Supports(QueryCapability.ScopedFields));
        Assert.True(required.Supports(QueryCapability.Negation));
    }

    [Fact]
    public void QueryCapabilityInspector_ReportsCaseInsensitiveStringComparison()
    {
        var caseSensitive = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField("status"),
                new ContainsValuePredicate("active")));
        var caseInsensitive = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField("status"),
                new ContainsValuePredicate("active", CaseSensitive: false)));

        Assert.False(QueryCapabilityInspector
            .GetRequiredCapabilities(caseSensitive)
            .Supports(QueryCapability.CaseInsensitiveStringComparison));
        Assert.True(QueryCapabilityInspector
            .GetRequiredCapabilities(caseInsensitive)
            .Supports(QueryCapability.CaseInsensitiveStringComparison));
    }

    [Fact]
    public async Task QueryBuilder_ExecutesRootQueries_SourcePredicates_AndPostJoinFilters()
    {
        var repositories = new DispatchingReadRepositoryRegistry()
            .Register(
                CustomerSource,
                InMemoryReadRepository.From<QueriedCustomerRecord>(
                    [
                        new("customer-1", "segment-enterprise", "active"),
                        new("customer-2", "segment-consumer", "active"),
                        new("customer-3", "segment-enterprise", "inactive")
                    ],
                    idSelector: static customer => customer.CustomerId))
            .Register(
                SegmentSource,
                InMemoryReadRepository.From<SegmentRecord>(
                    [
                        new("segment-enterprise", "enterprise"),
                        new("segment-consumer", "consumer")
                    ],
                    idSelector: static segment => segment.SegmentId))
            .Register(
                FilteredOrderSource,
                InMemoryReadRepository.From<OrderQueryRecord>(
                    [
                        new("order-1", "customer-1", 99.0m, "paid"),
                        new("order-2", "customer-1", 10.0m, "draft"),
                        new("order-3", "customer-2", 80.0m, "paid"),
                        new("order-4", "customer-3", 120.0m, "paid")
                    ],
                    idSelector: static order => order.OrderId)
                );

        var query = 
            Query.From(
                CustomerSource,
                new(new FieldPredicate(
                    FieldPath.FromField("Status"),
                    new ExactValuePredicate("active")
                )),
                fields: FieldSelection.ForFields("CustomerId", "SegmentId")
            )
            .JoinOne<QueriedCustomerRecord, string>(
                alias: "segment",
                source: SegmentSource,
                rootKeySelector: static customer => customer.SegmentId,
                options: FieldSelection.ForFields("SegmentId", "DisplayName"),
                sourcePredicate: new(
                    new FieldPredicate(
                        FieldPath.FromField("DisplayName"),
                        new ExactValuePredicate("enterprise")
                    )
                )
            )
            .JoinMany<QueriedCustomerRecord, OrderQueryRecord, string>(
                alias: "orders",
                source: FilteredOrderSource,
                rootKey: static customer => customer.CustomerId,
                foreignKey: static order => order.CustomerId,
                options: FieldSelection.ForFields("OrderId", "Total"),
                sourcePredicate: new(
                    new FieldPredicate(
                        FieldPath.FromField("Status"),
                        new ExactValuePredicate("paid"))
                )
            )
            .Where(new(new And<FieldPredicate>(
                [
                    new FieldPredicate(
                        FieldPath.Parse("segment.DisplayName"),
                        new ExactValuePredicate("enterprise")),
                    new FieldPredicate(
                        FieldPath.FromField("orders"),
                        new AnyFieldPredicate(
                            new FieldPredicate(
                                FieldPath.FromField("Total"),
                                NumberRangeValuePredicate.GreaterThan(50))))
                ]))
            )
            .Select(ctx => new FilteredCustomerProjection(
                    CustomerId: ctx.Root.GetField("CustomerId").GetString()!,
                    Segment: ctx.RequireOne<SegmentRecord>("segment"),
                    Orders: ctx.Many<FilteredOrderRecord>("orders")
                )
            );

        var result = await query.ExecuteAsync(OperationContext.Create(), repositories);

        var projection = Assert.Single(result);
        Assert.Equal("customer-1", projection.CustomerId);
        Assert.Equal("enterprise", projection.Segment.DisplayName);
        var order = Assert.Single(projection.Orders);
        Assert.Equal("order-1", order.OrderId);
        Assert.Equal(99.0m, order.Total);
    }

    [Fact]
    public async Task QueryBuilder_JoinMany_WithPointReadRepository_UsesIdFallback()
    {
        var repositories = new DispatchingReadRepositoryRegistry()
            .Register(
                OrderSource,
                new PointReadOrderRepository(
                [
                    new("order-1", "paid", 42.5m),
                    new("order-2", "draft", 13.0m)
                ]));

        var query = Query.From<FavoriteOrderRoot>(
                [new("root-1", "order-1")],
                rootId: static root => root.RootId
            )
            .JoinMany<FavoriteOrderRoot, PointReadOrderRecord, string>(
                alias: "orders",
                source: OrderSource,
                rootKey: static root => root.OrderId,
                foreignKey: static order => order.Id,
                sourcePredicate: new(
                    new FieldPredicate(
                        FieldPath.FromField("Status"),
                        new ExactValuePredicate("paid"))))
            .Select(ctx => ctx.Many<PointReadOrderRecord>("orders"));

        var result = await query.ExecuteAsync(OperationContext.Create(), repositories);

        var orders = Assert.Single(result);
        var order = Assert.Single(orders);
        Assert.Equal("order-1", order.Id);
        Assert.Equal("paid", order.Status);
    }

    [Fact]
    public async Task QueryBuilder_JoinMany_CanResolveRootKeyPathsAcrossArrays()
    {
        var repositories = new DispatchingReadRepositoryRegistry()
            .Register(
                BoundOrderSource,
                InMemoryReadRepository.From<PointReadOrderRecord>(
                [
                    new("projection-a", "paid", 42.5m),
                    new("projection-b", "ready", 13.0m),
                    new("projection-c", "draft", 7.0m)
                ],
                idSelector: static order => order.Id));

        var query = Query.From<ProjectionPolicyRoot>(
                [new("policy-1", [new("projection-a"), new("projection-b")])],
                rootId: static policy => policy.PolicyId
            )
            .JoinMany(
                alias: "orders",
                source: BoundOrderSource,
                rootKeyPath: FieldPath.Parse("ProjectionBindings.[].ProjectionId"),
                foreignKeyField: "Id")
            .Select(ctx => ctx.Many<PointReadOrderRecord>("orders"));

        var result = await query.ExecuteAsync(OperationContext.Create(), repositories);

        var orders = Assert.Single(result);
        Assert.Equal(["projection-a", "projection-b"], orders.Select(static order => order.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task QueryBuilder_RootQuery_CanUsePointReadFallbackForIdPredicates()
    {
        var repositories = new DispatchingReadRepositoryRegistry()
            .Register(
                BoundOrderSource,
                new PointReadOrderRepository(
                [
                    new("projection-a", "paid", 42.5m),
                    new("projection-b", "ready", 13.0m)
                ]));

        var query = Query.From(
                BoundOrderSource,
                new(new FieldPredicate(
                    FieldPath.FromField("Id"),
                    new ExactValuePredicate("projection-b"))))
            .Select(ctx => ctx.RootAs<PointReadOrderRecord>());

        var result = await query.ExecuteAsync(OperationContext.Create(), repositories);

        var order = Assert.Single(result);
        Assert.Equal("projection-b", order.Id);
        Assert.Equal("ready", order.Status);
    }

    [Fact]
    public void ScopedFieldPredicateEvaluator_EvaluatesScopedPredicatesInMemory()
    {
        var observation = new Observation(
            shapeId: new ShapeId("OrderBatch"),
            id: "batch-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Lines"] = ObservationValue.FromArray(
                [
                    ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        ["Status"] = ObservationValue.FromString("draft"),
                        ["Total"] = ObservationValue.FromDecimal(10m)
                    }),
                    ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        ["Status"] = ObservationValue.FromString("paid"),
                        ["Total"] = ObservationValue.FromDecimal(60m)
                    })
                ])
            });

        var query = new EntityPredicate(
            Predicate: new And<FieldPredicate>(
            [
                new FieldPredicate(
                    FieldPath.FromField("Status"),
                    new ExactValuePredicate("paid")),
                new FieldPredicate(
                    FieldPath.FromField("Total"),
                    NumberRangeValuePredicate.GreaterThan(50))
            ]),
            Scope: FieldPath.Parse("Lines.[]"));

        Assert.True(EntityPredicateEvaluator.Evaluate(observation, query));
    }

    [Fact]
    public void EntityPredicateEvaluator_HonorsStringPredicateCaseSensitivity()
    {
        var observation = new Observation(
            shapeId: new ShapeId("Sample"),
            id: "sample-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Name"] = ObservationValue.FromString("AlphaBeta")
            });

        Assert.False(EvaluateNamePredicate(observation, new PrefixValuePredicate("alpha")));
        Assert.True(EvaluateNamePredicate(observation, new PrefixValuePredicate("alpha", CaseSensitive: false)));

        Assert.False(EvaluateNamePredicate(observation, new SuffixValuePredicate("beta")));
        Assert.True(EvaluateNamePredicate(observation, new SuffixValuePredicate("beta", CaseSensitive: false)));

        Assert.False(EvaluateNamePredicate(observation, new ContainsValuePredicate("phab")));
        Assert.True(EvaluateNamePredicate(observation, new ContainsValuePredicate("phab", CaseSensitive: false)));

        Assert.False(EvaluateNamePredicate(observation, new ExactValuePredicate("alphabeta")));
        Assert.True(EvaluateNamePredicate(observation, new ExactValuePredicate("alphabeta", CaseSensitive: false)));
    }

    [Fact]
    public void EntityPredicateEvaluator_AnyValuePredicate_MatchesScalarArrayItems()
    {
        var observation = new Observation(
            shapeId: new ShapeId("Sample"),
            id: "sample-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Tags"] = ObservationValue.FromArray(
                [
                    ObservationValue.FromString("Canonical"),
                    ObservationValue.FromString("Production")
                ])
            });

        var query = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField("Tags"),
                new AnyValuePredicate(new ExactValuePredicate("canonical", CaseSensitive: false))));

        Assert.True(EntityPredicateEvaluator.Evaluate(observation, query));
    }

    [Fact]
    public async Task InMemoryQueryRepository_Aggregate_EvaluatesBucketedStatistics()
    {
        var repository = InMemoryReadRepository.From<ProcessTaskRecord>(
        [
            new("task-1", "compile", "complete", 12, new DateTimeOffset(2026, 05, 04, 10, 0, 0, TimeSpan.Zero)),
            new("task-2", "compile", "failed", 5, new DateTimeOffset(2026, 05, 05, 11, 0, 0, TimeSpan.Zero)),
            new("task-3", "review", "complete", 8, new DateTimeOffset(2026, 05, 05, 12, 0, 0, TimeSpan.Zero)),
            new("task-4", "compile", "complete", 7, new DateTimeOffset(2026, 04, 30, 9, 0, 0, TimeSpan.Zero))
        ], static record => record.Id);
        var failed = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField(nameof(ProcessTaskRecord.Status)),
                new ExactValuePredicate("failed")));
        var thisWeek = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField(nameof(ProcessTaskRecord.StartedAt)),
                new DateRangeValuePredicate(new DateTimeOffset(2026, 05, 01, 0, 0, 0, TimeSpan.Zero), End: null)));
        var plan = new AggregationPlan(
            Roots:
            [
                new AggregationRoot(
                    Name: "byType",
                    Root: new TermsGroupAggregationPlan(
                        GroupByField: FieldPath.FromField(nameof(ProcessTaskRecord.ProcessType)),
                        Order: new("count", Descending: true)),
                    Statistics:
                    [
                        new CountAggregationStatistic(),
                        new CountIfAggregationStatistic("failedCount", failed),
                        new SumAggregationStatistic("totalSeconds", FieldPath.FromField(nameof(ProcessTaskRecord.DurationSeconds))),
                        new SumIfAggregationStatistic("failedSeconds", FieldPath.FromField(nameof(ProcessTaskRecord.DurationSeconds)), failed),
                        new TopHitAggregationStatistic("sample", [FieldPath.FromField(nameof(ProcessTaskRecord.Id))])
                    ])
            ],
            Predicate: thisWeek);

        var results = (await repository.Query(OperationContext.Create(), EntityQuery.FromAggregationPlan(plan)))
            .Aggregations!;

        var rows = results["byType"].Bucketed().Rows;
        Assert.Equal(["compile", "review"], rows.Select(static row => row.Key).ToArray());
        var compile = rows[0];
        Assert.Equal(2, compile.DocCount);
        Assert.Equal(2d, compile.Statistics["count"]);
        Assert.Equal(1d, compile.Statistics["failedCount"]);
        Assert.Equal(17d, compile.Statistics["totalSeconds"]);
        Assert.Equal(5d, compile.Statistics["failedSeconds"]);
        Assert.True(compile.Samples.ContainsKey("sample"));
    }

    [Fact]
    public async Task InMemoryQueryRepository_Execute_ReturnsRowsPageInfoAndAggregations()
    {
        var repository = InMemoryReadRepository.From<ProcessTaskRecord>(
        [
            new("task-1", "compile", "complete", 12, new DateTimeOffset(2026, 05, 04, 10, 0, 0, TimeSpan.Zero)),
            new("task-2", "compile", "failed", 5, new DateTimeOffset(2026, 05, 05, 11, 0, 0, TimeSpan.Zero)),
            new("task-3", "review", "complete", 8, new DateTimeOffset(2026, 05, 05, 12, 0, 0, TimeSpan.Zero)),
            new("task-4", "compile", "complete", 7, new DateTimeOffset(2026, 04, 30, 9, 0, 0, TimeSpan.Zero))
        ], static record => record.Id);
        var compile = new EntityPredicate(
            new FieldPredicate(
                FieldPath.FromField(nameof(ProcessTaskRecord.ProcessType)),
                new ExactValuePredicate("compile")));

        var response = await repository.Query(
            OperationContext.Create(),
            EntityQuery.ForRowsAndAggregations(
                compile,
                new(
                    Fields: FieldSelection.ForFields(nameof(ProcessTaskRecord.Id)),
                    Window: new(
                        Limit: 2,
                        Offset: 0,
                        OrderBy: [new(FieldPath.FromField(nameof(ProcessTaskRecord.Id)))],
                        Mode: ResultPaginationMode.Offset)),
                new(
                [
                    new AggregationRoot(
                        Name: "allCompile",
                        Root: new GlobalAggregationPlan(),
                        Statistics: [new CountAggregationStatistic()])
                ])));

        Assert.Equal(["task-1", "task-2"], response.Rows.Select(static row => row.Id).ToArray());
        Assert.Equal(3, response.PageInfo?.TotalCount);
        Assert.Equal(2, response.PageInfo?.Limit);
        Assert.True(response.PageInfo?.HasMore == true);
        Assert.Equal(3d, response.Aggregations?["allCompile"].Singleton().Row.Statistics["count"]);
        Assert.DoesNotContain(response.Rows, static row => row.TryGetField(nameof(ProcessTaskRecord.Status), out _));
    }

    static bool EvaluateNamePredicate(Observation observation, ValuePredicate predicate) =>
        EntityPredicateEvaluator.Evaluate(
            observation,
            new EntityPredicate(new FieldPredicate(FieldPath.FromField("Name"), predicate)));

    sealed record CustomerRecord(string CustomerId, string SegmentId);

    sealed record QueriedCustomerRecord(string CustomerId, string SegmentId, string Status);

    sealed record SegmentRecord(string SegmentId, string DisplayName);

    sealed record OrderRecord(string OrderId, string CustomerId, decimal Total);

    sealed record OrderQueryRecord(string OrderId, string CustomerId, decimal Total, string Status);

    sealed record FilteredOrderRecord(string OrderId, string CustomerId, decimal Total);

    sealed record CustomerProjection(
        string CustomerId,
        SegmentRecord Segment,
        IReadOnlyList<OrderRecord> Orders);

    sealed record FilteredCustomerProjection(
        string CustomerId,
        SegmentRecord Segment,
        IReadOnlyList<FilteredOrderRecord> Orders);

    sealed record FavoriteOrderRoot(string RootId, string OrderId);

    sealed record ProjectionPolicyRoot(string PolicyId, ProjectionBindingRoot[] ProjectionBindings);

    sealed record ProjectionBindingRoot(string ProjectionId);

    sealed record PointReadOrderRecord(string Id, string Status, decimal Total);

    sealed record ProcessTaskRecord(
        string Id,
        string ProcessType,
        string Status,
        int DurationSeconds,
        DateTimeOffset StartedAt
        );

    sealed class PointReadOrderRepository(IReadOnlyList<PointReadOrderRecord> records) : IReadRepository
    {
        readonly IReadOnlyDictionary<string, Observation> records = records
            .ToDictionary(
                static record => record.Id,
                static record => ShapeMappingContext.Default.Map(
                    record,
                    new ShapeId(nameof(PointReadOrderRecord))),
                StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, Observation>> GetByIds(
            OperationContext context,
            IReadOnlyCollection<string> ids,
            FieldSelection? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(ids);
            context.ThrowIfCancellationRequested();

            Dictionary<string, Observation> result = new(StringComparer.Ordinal);
            foreach (var id in ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                if (records.TryGetValue(id, out var observation))
                    result[id] = Project(observation, options);
            }

            return Task.FromResult<IReadOnlyDictionary<string, Observation>>(result);
        }

        static Observation Project(Observation observation, FieldSelection? fields)
        {
            if (fields?.Fields is null || fields.Fields.Count == 0)
                return observation;

            Dictionary<string, ObservationValue> projected = new(StringComparer.Ordinal);
            foreach (var field in fields.Fields)
            {
                if (observation.TryGetField(field, out var value))
                    projected[field] = value;
            }

            return new(
                shapeId: observation.ShapeId,
                id: observation.Id,
                fields: projected,
                version: observation.Version,
                lineage: observation.Lineage);
        }
    }
}
