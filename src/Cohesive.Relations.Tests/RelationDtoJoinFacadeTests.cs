using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Tests;

public sealed class RelationDtoJoinFacadeTests
{
    [Fact]
    public void CompactAuthoringIsExactlyTheExpandedCanonicalRelation()
    {
        var compact = Build(compact: true);
        var expanded = Build(compact: false);
        Assert.Equal(RelationQueryJsonSerializer.Serialize(expanded.CompilationRequest.DefinitionDocument),
            RelationQueryJsonSerializer.Serialize(compact.CompilationRequest.DefinitionDocument));
        Assert.Equal(expanded.Compilation.Plan!.Provenance.DefinitionFingerprint,
            compact.Compilation.Plan!.Provenance.DefinitionFingerprint);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expanded.AuthoringProvenance),
            System.Text.Json.JsonSerializer.Serialize(compact.AuthoringProvenance));
        Assert.IsType<RelationDefinition>(compact.CompilationRequest.DefinitionDocument.Definition);
    }

    [Fact]
    public async Task ExecutesTheThreeSourceExampleWithExactContributors()
    {
        var mapping = Build(compact: true);
        var evaluator = Evaluator(mapping);
        var evaluation = mapping.Evaluate(new("joined-dto/request-42")).Supply(Loads(), x => x.Id).Build();
        var outcome = await mapping.EvaluateAsync(evaluator, evaluation);
        Assert.True(outcome.IsSuccessful, Explain(outcome));
        Assert.Same(mapping.Compilation.Plan, outcome.Compilation.Plan);
        var row = Assert.Single(mapping.Mapper.Map(outcome.PhysicalExecution!).Rows);
        Assert.Equal(new Summary("load-42", "Acme", 125m), row.Value);
        Assert.Equal("load-42", row.Source.Root!.ObservationIdentity);
        Assert.Equal(new[] { "customer-7", "item-1", "item-2", "load-42" },
            row.Source.InputOccurrences.Select(x => x.ObservationIdentity!).Order(StringComparer.Ordinal));
        Assert.Equal([new Summary("load-42", "Acme", 125m)], await mapping.MapAsync(evaluator, evaluation));
        var expanded = Build(compact: false);
        Assert.Equal(await mapping.MapAsync(evaluator, evaluation), await expanded.MapAsync(Evaluator(expanded),
            expanded.Evaluate(new("expanded/request")).Supply(Loads(), x => x.Id).Build()));
    }

    [Fact]
    public async Task KeepsInnerOmissionsAndEnforcesExplicitPerRootCardinality()
    {
        var mapping = Build(compact: true);
        var outcome = await mapping.EvaluateAsync(Evaluator(mapping), mapping.Evaluate(new("omissions"))
            .Supply(Loads().Concat([new Load("no-items", "customer-7"), new Load("no-customer", "missing")]), x => x.Id).Build());
        Assert.True(outcome.IsSuccessful, Explain(outcome));
        Assert.Single(mapping.Mapper.Map(outcome.PhysicalExecution!).Rows);
        var required = Build(compact: true, mode: RelationOutputMode.OnePerRoot);
        var failed = required.Evaluate(new("required")).Supply([new Load("no-items", "customer-7")], x => x.Id).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => required.MapAsync(Evaluator(required), failed));
        var duplicateCustomer = mapping.Evaluate(new("fanout")).Supply(Loads(), x => x.Id).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapping.MapAsync(Evaluator(mapping, duplicateCustomer: true), duplicateCustomer));
    }

    [Fact]
    public async Task DoesNotTreatIncompleteItemsAsZeroAndRejectsForeignRequestsOrCancellation()
    {
        var mapping = Build(compact: true);
        var evaluation = mapping.Evaluate(new("incomplete")).Supply(Loads(), x => x.Id).Build();
        var evaluator = Evaluator(mapping, incompleteItems: true);
        var outcome = await mapping.EvaluateAsync(evaluator, evaluation);
        Assert.False(outcome.IsSuccessful);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapping.MapAsync(evaluator, evaluation));
        var foreign = Build(compact: true).Evaluate(new("foreign")).Supply(Loads(), x => x.Id).Build();
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, foreign));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.MapAsync(evaluator, evaluation, cancel.Token));
    }

    [Fact]
    public async Task EmptyItemsProduceNoGroupsAndSuppliedOnlyCannotPretendToAcquireOtherSources()
    {
        var mapping = Build(compact: true);
        var evaluation = mapping.Evaluate(new("empty-items")).Supply(Loads(), x => x.Id).Build();
        Assert.Empty(await mapping.MapAsync(Evaluator(mapping, emptyItems: true), evaluation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mapping.MapAsync(RelationQueryEvaluator.CreateSuppliedOnly(), evaluation));
    }

    [Fact]
    public void RejectsCrossSessionJoinsAndApproximateNumericSums()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>();
        var foreign = RelationQuery.Expression().Source<Customer>();
        Assert.Throws<ArgumentException>(() => loads.Join(foreign, (l, c) => l.CustomerId == c.Id,
            (l, c) => new LoadCustomer(l.Id, c.Name)));
        var foreignOutput = foreign.Select(c => new LoadCustomer(c.Id, c.Name));
        Assert.Throws<ArgumentException>(() => RelationDtoMapping.Create(loads, foreignOutput,
            RelationOutputMode.ZeroOrOnePerRoot));
        var approximate = q.Source<Approximate>();
        Assert.Throws<ArgumentException>(() => approximate.Aggregate<Approximate>(a => a.Sum(x => x.Value, x => x.Value)));
        var integer = q.Source<IntegerInput>();
        var sumFailure = Assert.Throws<ArgumentException>(() => integer.Aggregate<DecimalTotal>(a =>
            a.Sum(t => t.Total, i => i.Quantity)));
        Assert.Contains("Sum requires", sumFailure.Message);
        var groupFailure = Assert.Throws<ArgumentException>(() => integer.Aggregate<DecimalTotal>(a =>
            a.Group(t => t.Total, i => i.Quantity).Sum(t => t.Total, i => (decimal)i.Quantity)));
        Assert.Contains("does not match", groupFailure.Message);
    }

    [Fact]
    public void ProjectedLeftJoinKeepsTheExactExistingMissingValueSemantics()
    {
        string Define(bool compact)
        {
            var q = RelationQuery.Expression(); var loads = q.Source<Load>(); var customers = q.Source<Customer>();
            var output = compact
                ? loads.LeftJoin(customers, (l, c) => l.CustomerId == c.Id, (l, c) => new LoadCustomer(l.Id, c.Name))
                : loads.LeftJoin(customers, (l, c) => l.CustomerId == c.Id).Select((l, c) => new LoadCustomer(l.Id, c.Name));
            return RelationQueryJsonSerializer.Serialize(q.BuildRelation(loads, output, RelationOutputMode.ZeroOrOnePerRoot).CreateDocument());
        }
        Assert.Equal(Define(false), Define(true));
    }

    static RelationDtoMapping<Summary> Build(bool compact, RelationOutputMode mode = RelationOutputMode.ZeroOrOnePerRoot)
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>(); var items = q.Source<LineItem>(); var customers = q.Source<Customer>();
        var totals = compact
            ? items.Aggregate<LoadTotal>(g => g.Group(t => t.LoadId, i => i.LoadId)
                .Sum(t => t.Total, i => i.Quantity * i.UnitPrice))
            : q.Aggregate(items.Node, q.Clr.Shape<LoadTotal>(), g => g
                .Group(t => t.LoadId, (LineItem i) => i.LoadId, items.Binding)
                .Value(t => t.Total, AggregateOperator.Sum, (LineItem i) => i.Quantity * i.UnitPrice, items.Binding));
        var withCustomers = compact
            ? loads.Join(customers, (l, c) => l.CustomerId == c.Id, (l, c) => new LoadCustomer(l.Id, c.Name))
            : loads.Join(customers, (l, c) => l.CustomerId == c.Id).Select((l, c) => new LoadCustomer(l.Id, c.Name));
        var output = compact
            ? withCustomers.Join(totals, (l, t) => l.Id == t.LoadId, (l, t) => new Summary(l.Id, l.CustomerName, t.Total))
            : withCustomers.Join(totals, (l, t) => l.Id == t.LoadId).Select((l, t) => new Summary(l.Id, l.CustomerName, t.Total));
        return RelationDtoMapping.Create(loads, output, mode);
    }

    static Load[] Loads() => [new("load-42", "customer-7")];
    static string Explain(RelationQueryEvaluationOutcome x) => string.Join("\n",
        x.Compilation.Diagnostics.Select(d => d.Message).Concat(x.PhysicalPlanning?.Diagnostics.Select(d => d.Message) ?? [])
            .Concat(x.PhysicalExecution?.Diagnostics.Select(d => d.Message) ?? []).Concat(x.Result?.Diagnostics.Select(d => d.Message) ?? []));

    static RelationQueryEvaluator Evaluator(RelationDtoMapping<Summary> mapping, bool incompleteItems = false,
        bool duplicateCustomer = false, bool emptyItems = false)
    {
        var plan = mapping.Compilation.Plan!;
        var builder = RelationQueryPlacement.For(plan);
        foreach (var input in plan.InputContract.Sources)
        {
            var source = builder.Source(input.Binding.Value, ReaderProfile(),
                limits: new(maximumBatchSize: 100, maximumBufferedRows: 100, maximumFanOut: 100, maximumConcurrency: 1));
            var placed = builder.Place(input, source).FieldsBySemanticPath();
            if (input.Role == RelationQuerySourceInputRole.RelationRoot
                || input.Shape.ShapeId.Value.Contains(nameof(Customer), StringComparison.Ordinal))
                placed.Identity(FieldPath.FromField("Id"), RelationQueryPlacementBuilder.FrameworkIdentitySourceSelector);
        }
        var placement = builder.Build().RequireValue().Placement;
        var readers = new List<IRelationQuerySourceReader>();
        foreach (var binding in placement.Bindings.Where(x => x.Acquisition != RelationQuerySourceAcquisitionKind.Supplied))
        {
            var source = placement.SourceInstances.Single(x => x.Id == binding.Source);
            var itemSource = binding.Shape.ShapeId.Value.Contains(nameof(LineItem), StringComparison.Ordinal);
            ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> rows = itemSource
                ? [Item("item-1", "load-42", 2m, 50m), Item("item-2", "load-42", 1m, 25m), Item("orphan", "no-customer", 1m, 10m)]
                : [CustomerRow("customer-7")];
            if (itemSource && emptyItems) rows = [];
            // A second customer with the same join key is represented by a distinct provider identity.
            if (duplicateCustomer && !itemSource)
                rows = [CustomerRow("customer-7"), Row("provider-customer-duplicate", ("Id", ObservationValue.FromString("customer-7")), ("Name", ObservationValue.FromString("Duplicate")))];
            readers.Add(new DeterministicRelationQuerySourceReader(new(source.Id, source.ExecutionDomain, source.TargetProfile,
                RelationQueryLogicalPartitionIdentity.WholeSource), rows,
                resultFactory: itemSource && incompleteItems ? _ => new(RelationQuerySourceReadState.Inconclusive) : null));
        }
        return new(_ => placement, FederatedLoadPhysicalExecutionFixture.CreatePolicy(maximumBatchSize: 100), readers);
    }

    static RelationQueryTargetCapabilityProfile ReaderProfile() => new(
        new("tests/join-reader"), new("tests/join-reader/v1"), [RelationQueryDocument.CurrentSchemaVersion],
        [RelationQueryCompilationProvenance.CurrentCompilerProfile],
        [.. new[] { RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.FieldProjection, RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead }
            .Select(kind => new RelationQueryTargetCapabilityEvidence(new($"tests/reader/{kind}"), new PrimitiveRelationQueryCapability(kind)))]);

    static DeterministicRelationQuerySourceReader.SourceRow Item(string id, string load, decimal quantity, decimal price) => Row(id,
        ("Id", ObservationValue.FromString(id)), ("LoadId", ObservationValue.FromString(load)),
        ("Quantity", ObservationValue.FromDecimal(quantity)), ("UnitPrice", ObservationValue.FromDecimal(price)));
    static DeterministicRelationQuerySourceReader.SourceRow CustomerRow(string id) => Row(id,
        ("Id", ObservationValue.FromString(id)), ("Name", ObservationValue.FromString("Acme")));
    static DeterministicRelationQuerySourceReader.SourceRow Row(string id, params (string Name, ObservationValue Value)[] fields) =>
        DeterministicRelationQuerySourceReader.SourceRow.Create(id, [.. fields.Select(x => (FieldPath.FromField(x.Name), x.Value))]);

    public sealed record Load(string Id, string CustomerId);
    public sealed record Customer(string Id, string Name);
    public sealed record LineItem(string Id, string LoadId, decimal Quantity, decimal UnitPrice);
    public sealed record LoadTotal(string LoadId, decimal Total);
    public sealed record LoadCustomer(string Id, string CustomerName);
    public sealed record Summary(string Id, string CustomerName, decimal Total);
    public sealed record Approximate(double Value);
    public sealed record IntegerInput(int Quantity);
    public sealed record DecimalTotal(decimal Total);
}
