using System.Collections.Immutable;
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
        .AddSingleton(new OrderReadPolicy { Source = OrderDetailsReadSource.Materialized, AcceptEventualConsistency = true }));
    static ServiceQuerySelection Details(FulfillmentApplication model) => model.Endpoints.Plan.QueryDependencies.Queries.Single(
        item => item.Request.DefinitionDocument.DefinitionFingerprint == model.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
    static readonly ServiceQueryReadBinding Elastic = new("Elasticsearch", "orders-materialized", RelationQueryConsistency.Eventual);

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
        Assert.Throws<ArgumentException>(() => selected.SelectQuery(Model().Queries.OrderDetails, Elastic, (_, _) => throw new ServingFailure()));
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
        { NativeQueries++; throw new NotSupportedException(); }
    }
}
