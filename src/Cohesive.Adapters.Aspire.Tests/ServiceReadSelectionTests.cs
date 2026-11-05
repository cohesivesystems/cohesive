using System.Collections.Immutable;
using Cohesive.Configuration;
using AspireFirst.Orders;
using Cohesive.Api.Execution.Services;
using Cohesive.Application;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ServiceReadSelectionTests
{
    static FulfillmentApplication Model() => ModelActivator.Create<FulfillmentApplication>(services => services
        .AddSingleton(new FulfillmentHistoryPolicy())
        .AddSingleton(new RelationQueryReadPolicy { Source = RelationQueryReadSource.Materialized, AcceptEventualConsistency = true }));
    static ServiceQuerySelection Details(FulfillmentApplication model) => model.Endpoints.Plan.QueryDependencies.Queries.Single(
        item => item.Request.DefinitionDocument.DefinitionFingerprint == model.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
    static readonly ServiceQueryReadBinding Elastic = new("Elasticsearch", "orders-materialized", RelationQueryConsistency.Eventual);

    [Fact]
    public void Flag_over_nonfixed_native_selection_has_structured_admission_error()
    {
        var model = Model();
        var source = new Source(model) { ReadSelection = new ServiceQueryFeatureSelection("other", Elastic, Elastic) };
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic,
            (_, _) => throw new ServingFailure(), ServiceReadSelection<string>.ByFeatureFlag<string>("reads", _ => value => value));
        var error = Assert.Throws<ServiceBindingValidationException>(() => selected.PrepareQueries([Details(model)]));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == ServiceBindingDiagnosticCodes.QueryPlacementUnsupported);
        Assert.Equal(0, source.Binds);
    }

    [Fact]
    public async Task Warm_fallback_through_storage_composition_reuses_the_same_canonical_identity()
    {
        var model = Model();
        var source = new Source(model) { SelectedReader = new Reader(model.Queries.OrderDetails, RelationQueryConsistency.Eventual) };
        var composition = new ServiceStorageComposition([source]);
        composition.Validate([model.Endpoints.Plan]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var persistence = new ServicePersistenceBuilder(await composition.BindAsync(services)).Build();
        for (var i = 0; i < 20; i++) persistence.Query(model.Queries.OrderDetails, 100, 10000, RelationQueryConsistency.Eventual);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) persistence.Query(model.Queries.OrderDetails, 100, 10000, RelationQueryConsistency.Eventual);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void Warm_canonical_query_resolution_has_bounded_allocation()
    {
        var query = Model().Queries.OrderDetails;
        var reader = new Reader(query, RelationQueryConsistency.Eventual);
        var persistence = new ServicePersistenceBuilder().Query(query, (_, _) => reader).Build();
        for (var i = 0; i < 20; i++) persistence.Query(query, 100, 10000, RelationQueryConsistency.Eventual);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) persistence.Query(query, 100, 10000, RelationQueryConsistency.Eventual);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task Canonically_equal_queries_share_one_preparation_and_keep_serving_failure_without_fallback()
    {
        var model = Model();
        var source = new Source(model);
        var preparations = 0;
        var factories = 0;
        var reader = new Reader(model.Queries.OrderDetails, RelationQueryConsistency.Eventual);
        var selection = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) =>
        {
            preparations++;
            return ValueTask.FromResult<Func<int, long, IRelationQueryReader<string, OrderDetails?>>>((_, _) => { factories++; return reader; });
        });
        var query = Details(model);
        var admitted = selection.PrepareQueries([query]);
        Assert.Equal(Elastic, admitted.DescribeRead(query));
        Assert.Empty(source.Queries);
        Assert.Equal(0, preparations);
        using var services = new ServiceCollection().BuildServiceProvider();
        var persistence = await admitted.BindAsync(services);
        var independentlyBuilt = Model().Queries.OrderDetails;
        Assert.NotSame(independentlyBuilt, model.Queries.OrderDetails);
        var actual = persistence.Query(independentlyBuilt, query.MaximumRows, query.MaximumBytes, query.Consistency);
        Assert.Same(reader, actual);
        Assert.Same(actual, persistence.Query(model.Queries.OrderDetails, query.MaximumRows, query.MaximumBytes, query.Consistency));
        Assert.Equal(1, preparations);
        Assert.Equal(1, factories);
        Assert.Equal(1, source.Binds);
        await Assert.ThrowsAsync<ServingFailure>(() => actual.ReadAsync("order"));
        Assert.Equal(0, source.NativeQueries);
        Assert.Throws<InvalidOperationException>(() => persistence.Query(independentlyBuilt, 1, 1, query.Consistency));
    }

    [Fact]
    public void Weaker_declaration_fails_before_any_native_preparation()
    {
        var model = TestModels.Fulfillment();
        var source = new Source(model);
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure());
        Assert.Throws<ServiceBindingValidationException>(() => selected.PrepareQueries([Details(model)]));
        Assert.Equal(0, source.Binds);
    }

    [Fact]
    public async Task Concrete_reader_must_meet_its_declared_guarantee_before_source_schema_preparation()
    {
        var model = Model();
        var source = new Source(model);
        var selected = source.SelectQuery(model.Queries.OrderDetails,
            new("declared-snapshot", "read-target", RelationQueryConsistency.SingleSnapshot),
            (_, _) => ValueTask.FromResult<Func<int, long, IRelationQueryReader<string, OrderDetails?>>>(
                (_, _) => new Reader(model.Queries.OrderDetails, RelationQueryConsistency.Eventual)));
        using var services = new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAsync<ServiceBindingValidationException>(() => selected.PrepareQueries([Details(model)]).BindAsync(services).AsTask());
        Assert.Equal(0, source.Binds);
    }

    [Fact]
    public void Unused_selection_does_not_prepare_or_require_native_target()
    {
        var model = Model();
        var source = new Source(model);
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure());
        Assert.Same(source, selected.PrepareQueries([]));
    }

    [Fact]
    public void Repeating_the_same_canonical_selection_is_rejected()
    {
        var model = Model();
        var selected = new Source(model).SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure());
        Assert.Throws<ServiceBindingValidationException>(() => selected.SelectQuery(Model().Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure()));
    }

    [Fact]
    public async Task Live_flag_changes_route_subsequent_reads_without_preparing_again()
    {
        var model = Model();
        var live = new CountingReader(model.Queries.OrderDetails);
        var alternate = new CountingReader(model.Queries.OrderDetails);
        var source = new Source(model) { SelectedReader = live };
        var evaluator = new Flags();
        var preparations = 0;
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) =>
        {
            preparations++;
            return ValueTask.FromResult<Func<int, long, IRelationQueryReader<string, OrderDetails?>>>((_, _) => alternate);
        }, selection: FlagChoice());
        var demand = Details(model);
        var prepared = selected.PrepareQueries([demand]);
        var explanation = Assert.IsType<ServiceQueryFeatureSelection>(prepared.DescribeRead(demand));
        Assert.Equal("details", explanation.Flag);
        Assert.Equal("Test", explanation.Disabled.Backend);
        Assert.Equal(Elastic, explanation.Enabled);
        using var services = new ServiceCollection().AddSingleton<IFeatureFlagEvaluator<string>>(evaluator).BuildServiceProvider();
        var persistence = await prepared.BindAsync(services);
        var reader = persistence.Query(model.Queries.OrderDetails, demand.MaximumRows, demand.MaximumBytes, demand.Consistency);
        await reader.ReadAsync("a");
        evaluator.Enabled = true;
        await reader.ReadAsync("b");
        evaluator.Enabled = false;
        await reader.ReadAsync("c");
        Assert.Equal(2, live.Calls);
        Assert.Equal(1, alternate.Calls);
        Assert.Equal(3, evaluator.Calls);
        Assert.Equal(1, evaluator.Admissions);
        Assert.Equal(1, preparations);
        Assert.Equal(1, source.Binds);
        Assert.Equal(1, source.NativeQueries);
    }

    [Fact]
    public async Task In_flight_read_keeps_its_decision_and_concurrent_inputs_are_isolated()
    {
        var model = Model();
        var release = new TaskCompletionSource<OrderDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = new CountingReader(model.Queries.OrderDetails) { Result = release.Task };
        var alternate = new CountingReader(model.Queries.OrderDetails);
        var flags = new Flags();
        var reader = await BindFlag(model, live, alternate, flags);
        var pending = reader.ReadAsync("first");
        flags.Enabled = true;
        await reader.ReadAsync("second");
        Assert.False(pending.IsCompleted);
        release.SetResult(null);
        await pending;
        Assert.Equal("first", live.LastInput);
        Assert.Equal("second", alternate.LastInput);
        Assert.Equal(2, flags.Calls);
    }

    [Fact]
    public async Task Provider_and_selected_backend_failures_do_not_trigger_fallback()
    {
        var model = Model();
        var live = new CountingReader(model.Queries.OrderDetails);
        var alternate = new CountingReader(model.Queries.OrderDetails) { Result = Task.FromException<OrderDetails?>(new ServingFailure()) };
        var flags = new Flags { Enabled = true };
        var reader = await BindFlag(model, live, alternate, flags);
        await Assert.ThrowsAsync<ServingFailure>(() => reader.ReadAsync("a"));
        Assert.Equal(0, live.Calls);
        flags.Error = new ServingFailure();
        await Assert.ThrowsAsync<ServingFailure>(() => reader.ReadAsync("b"));
        Assert.Equal(1, alternate.Calls);
        Assert.Equal(0, live.Calls);
    }

    [Fact]
    public async Task Cancellation_after_async_decision_prevents_backend_dispatch()
    {
        var model = Model();
        var live = new CountingReader(model.Queries.OrderDetails);
        var alternate = new CountingReader(model.Queries.OrderDetails);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = new Flags { Pending = decision.Task };
        var reader = await BindFlag(model, live, alternate, flags);
        using var cancellation = new CancellationTokenSource();
        var pending = reader.ReadAsync("a", cancellation.Token);
        cancellation.Cancel();
        decision.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, live.Calls + alternate.Calls);
    }

    [Fact]
    public async Task Missing_flag_provider_fails_before_native_preparation()
    {
        var model = Model();
        var source = new Source(model);
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure(), selection: FlagChoice());
        using var services = new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected.PrepareQueries([Details(model)]).BindAsync(services).AsTask());
        Assert.Equal(0, source.Binds);
    }

    [Fact]
    public void Flag_does_not_weaken_snapshot_requirement_even_when_disabled()
    {
        var model = TestModels.Fulfillment();
        var source = new Source(model);
        var selected = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure(), selection: FlagChoice());
        Assert.Throws<ServiceBindingValidationException>(() => selected.PrepareQueries([Details(model)]));
        Assert.Equal(0, source.Binds);
    }

    [Fact]
    public async Task Warm_synchronous_flag_dispatch_adds_no_managed_allocation()
    {
        var model = Model();
        var reader = await BindFlag(model, new CountingReader(model.Queries.OrderDetails), new CountingReader(model.Queries.OrderDetails), new Flags());
        for (var i = 0; i < 100; i++) await reader.ReadAsync("a");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) await reader.ReadAsync("a");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task Missing_configured_flag_fails_before_targeting_or_either_native_preparation()
    {
        var model = Model();
        var source = new Source(model);
        var flags = new Flags { Missing = true };
        var selection = source.SelectQuery(model.Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure(),
            selection: ServiceReadSelection<string>.ByFeatureFlag<string>("missing", _ => throw new ServingFailure()));
        using var services = new ServiceCollection().AddSingleton<IFeatureFlagEvaluator<string>>(flags).BuildServiceProvider();
        var error = await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => selection.PrepareQueries([Details(model)]).BindAsync(services).AsTask());
        Assert.Equal("missing", error.Flag);
        Assert.Equal(0, source.Binds);
        Assert.Equal(0, flags.Calls);
    }

    [Fact]
    public async Task Targeting_contract_is_independent_of_query_input_and_isolated_between_concurrent_tenants()
    {
        var model = Model();
        var live = new CountingReader(model.Queries.OrderDetails);
        var alternate = new CountingReader(model.Queries.OrderDetails);
        var source = new Source(model) { SelectedReader = live };
        var accessor = new TenantAccessor();
        var preparations = 0;
        var selection = source.SelectQuery(model.Queries.OrderDetails, Elastic,
            (_, _) => ValueTask.FromResult<Func<int, long, IRelationQueryReader<string, OrderDetails?>>>((_, _) => alternate),
            selection: ServiceReadSelection<string>.ByFeatureFlag<Tenant>("details", services =>
            {
                preparations++;
                var context = services.GetRequiredService<TenantAccessor>();
                return _ => context.Current.Value ?? throw new InvalidOperationException("No tenant.");
            }));
        using var services = new ServiceCollection().AddSingleton(accessor).AddSingleton<IFeatureFlagEvaluator<Tenant>>(new TenantFlags()).BuildServiceProvider();
        var demand = Details(model);
        var persistence = await selection.PrepareQueries([demand]).BindAsync(services);
        var reader = persistence.Query(model.Queries.OrderDetails, demand.MaximumRows, demand.MaximumBytes, demand.Consistency);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            accessor.Current.Value = new Tenant(i % 2 == 0);
            await reader.ReadAsync("identical-order-id");
        })));
        Assert.Equal(50, live.Calls);
        Assert.Equal(50, alternate.Calls);
        Assert.Equal(1, preparations);
        Assert.Equal(1, source.Binds);
    }

    sealed record Tenant(bool Enabled);
    sealed class TenantAccessor { public AsyncLocal<Tenant?> Current { get; } = new(); }
    sealed class TenantFlags : IFeatureFlagEvaluator<Tenant>
    {
        public ValueTask RequireFlagAsync(string flag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> IsEnabledAsync(string flag, Tenant context, CancellationToken cancellationToken = default) => ValueTask.FromResult(context.Enabled);
    }

    static async Task<IRelationQueryReader<string, OrderDetails?>> BindFlag(FulfillmentApplication model,
        CountingReader live, CountingReader alternate, Flags flags)
    {
        var source = new Source(model) { SelectedReader = live };
        var selection = source.SelectQuery(model.Queries.OrderDetails, Elastic,
            (_, _) => ValueTask.FromResult<Func<int, long, IRelationQueryReader<string, OrderDetails?>>>((_, _) => alternate), selection: FlagChoice());
        using var services = new ServiceCollection().AddSingleton<IFeatureFlagEvaluator<string>>(flags).BuildServiceProvider();
        var demand = Details(model);
        var persistence = await selection.PrepareQueries([demand]).BindAsync(services);
        return persistence.Query(model.Queries.OrderDetails, demand.MaximumRows, demand.MaximumBytes, demand.Consistency);
    }

    static ServiceReadSelection<string> FlagChoice() => ServiceReadSelection<string>.ByFeatureFlag<string>("details", _ => input => input);

    sealed class Flags : IFeatureFlagEvaluator<string>
    {
        public bool Enabled;
        public int Admissions;
        public bool Missing;
        public ValueTask RequireFlagAsync(string flag, CancellationToken cancellationToken = default)
        { Admissions++; if (Missing) throw new FeatureFlagNotFoundException(flag); return ValueTask.CompletedTask; }
        public int Calls;
        public Exception? Error;
        public Task<bool>? Pending;
        public ValueTask<bool> IsEnabledAsync(string flag, string input, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Pending is null ? ValueTask.FromResult(Enabled) : new(Pending);
        }
    }
    sealed class CountingReader(RelationQuery<string, OrderDetails?> definition) : IRelationQueryReader<string, OrderDetails?>
    {
        public RelationQuery<string, OrderDetails?> Definition => definition;
        public RelationQueryConsistency Consistency => RelationQueryConsistency.Eventual;
        public Task<OrderDetails?> Result = Task.FromResult<OrderDetails?>(null);
        public int Calls;
        public string? LastInput;
        public Task<OrderDetails?> ReadAsync(string input, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); LastInput = input; return Result; }
    }

    sealed class ServingFailure : Exception;
    sealed class Reader(RelationQuery<string, OrderDetails?> definition, RelationQueryConsistency consistency) : IRelationQueryReader<string, OrderDetails?>
    {
        public RelationQueryConsistency Consistency => consistency;
        public RelationQuery<string, OrderDetails?> Definition => definition;
        public Task<OrderDetails?> ReadAsync(string input, CancellationToken cancellationToken = default) => throw new ServingFailure();
    }
    sealed class Source(FulfillmentApplication model) : IServiceStorageComposition, IServicePersistence
    {
        public ServiceQueryReadSelection? ReadSelection { get; init; }
        public ServiceQueryReadSelection DescribeRead(ServiceQuerySelection query) =>
            ReadSelection ?? new ServiceQueryReadBinding("Test", "test-resource", Cohesive.Relations.Execution.RelationQueryConsistency.SingleSnapshot);

        public object? SelectedReader { get; init; }
        public int Binds { get; private set; }
        public int NativeQueries { get; private set; }
        public ImmutableArray<ServiceQuerySelection> Queries { get; private set; } = [];
        public ImmutableArray<ValueContract> Observations => model.Storage.Native.Observations;
        public ImmutableArray<QualifiedShapeId> Shapes => model.Storage.Native.Shapes;
        public ImmutableArray<object> Bindings => model.Storage.Native.Bindings;
        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> queries) { Queries = queries; return this; }
        public ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default) { Binds++; return ValueTask.FromResult<IServicePersistence>(this); }
        public IEntityRepository Repository(ValueContract observation) => throw new NotSupportedException();
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
        { NativeQueries++; return SelectedReader as IRelationQueryReader<TInput, TResult> ?? throw new NotSupportedException(); }
    }
}
