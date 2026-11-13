using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
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
    public async Task DefaultGroupedSumAndCollectionBindingsPreserveResultsAndContributors()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>();
        var summaries = loads
            .Join(q.Source<LineItem>().SumBy(i => i.LoadId, i => i.Quantity * i.UnitPrice),
                (l, t) => l.Id == t.Key,
                (l, t) => new { l.Id, l.CustomerId, t.Total })
            .Join(q.Source<Customer>(), (l, c) => l.CustomerId == c.Id,
                (l, c) => new Summary(l.Id, c.Name, l.Total));
        var mapping = RelationDtoMapping.Create(loads, summaries, RelationOutputMode.ZeroOrOnePerRoot);
        Customer[] customers = [new("customer-7", "Acme")];
        LineItem[] items =
        [
            new("item-1", "load-42", 2m, 50m),
            new("item-2", "load-42", 1m, 25m),
            new("item-3", "load-é", 1m, 15m)
        ];
        var evaluator = mapping.CreateInMemoryEvaluator(customers, items);

        var empty = mapping.Evaluate(new("compile")).Supply(Array.Empty<Load>(), load => load.Id).Build();
        var preparation = await mapping.EvaluateAsync(evaluator, empty);
        Assert.True(preparation.IsSuccessful, Explain(preparation));
        Assert.NotNull(PreparedLocalDtoKernel<Summary>.TryCompile(mapping.Compilation.Plan!, preparation, mapping.Mapper, nameof(Load.Id)));

        var outcome = await mapping.EvaluateAsync(evaluator, Loads(), load => load.Id);
        Assert.True(outcome.IsSuccessful, Explain(outcome));
        var row = Assert.Single(mapping.Mapper.Map(outcome.PhysicalExecution!).Rows);
        Assert.Equal(new Summary("load-42", "Acme", 125m), row.Value);
        Assert.Equal(new[] { "customer-7", "item-1", "item-2", "load-42" },
            row.Source.InputOccurrences.Select(x => x.ObservationIdentity!).Order(StringComparer.Ordinal));
        Assert.Equal(row.Value, Assert.Single(await mapping.MapAsync(evaluator, Loads())));

        var rawMapper = RelationDtoMapperCompiler.Default.Compile<RawSummary>(mapping.Compilation.Plan!).Mapper!;
        var rawKernel = Assert.IsType<PreparedLocalDtoKernel<RawSummary>>(PreparedLocalDtoKernel<RawSummary>.TryCompile(
            mapping.Compilation.Plan!, preparation, rawMapper, nameof(Load.Id)));
        Assert.True(rawKernel.TryMap(Loads(), CancellationToken.None, out var rawValues, out _));
        var rawReference = Assert.Single(rawMapper.Map(outcome.PhysicalExecution!).Rows).Value;
        Assert.Equal(ObservationValueKind.Int64, rawReference.Total.Kind);
        Assert.Equal(rawReference, Assert.Single(rawValues));

        Assert.Empty(await mapping.MapAsync(evaluator, [new Load("no-items", "customer-7")]));
        var twoLoads = Loads().Append(new Load("load-é", "customer-7")).ToArray();
        var canonical = await mapping.MapAsync(evaluator, mapping.Evaluate(new("canonical")).Supply(twoLoads, load => load.Id).Build());
        Assert.Equal(new[] { "load-é", "load-42" }, canonical.Select(dto => dto.Id));
        Assert.Equal(canonical, await mapping.MapAsync(evaluator, twoLoads));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, twoLoads.Concat(twoLoads)));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, [new Load("load-42", null!)]));

        Assert.Equal(row.Value, Assert.Single(await mapping.MapAsync(mapping.CreateInMemoryEvaluator(items, customers), Loads())));
        Assert.Throws<ArgumentException>(() => mapping.CreateInMemoryEvaluator(Loads(), items));
        Assert.Throws<ArgumentException>(() => mapping.CreateInMemoryEvaluator(customers.Concat(customers), items));
        Assert.Throws<ArgumentException>(() => mapping.CreateInMemoryEvaluator(customers, items, maximumRows: 1));
    }

    [Fact]
    public async Task DefaultRootRequiresAnIdentityFieldAndAllowsAnExplicitOverride()
    {
        var q = RelationQuery.Expression();
        var roots = q.Source<KeyedLoad>();
        var output = roots.Select(load => new Summary(load.Key, load.Customer, 0m));
        var mapping = RelationDtoMapping.Create(roots, output, RelationOutputMode.OnePerRoot);

        Assert.Throws<InvalidOperationException>(() => mapping.CreateInMemoryEvaluator(_ => { }));
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources.Root(q.Clr.Shape<KeyedLoad>(), load => load.Key));
        Assert.Equal(new Summary("load-42", "Acme", 0m),
            Assert.Single(await mapping.MapAsync(evaluator, [new KeyedLoad("load-42", "Acme")])));
    }

    sealed record KeyedLoad(string Key, string Customer);

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
    public async Task CollectionBindingsUseOwnedSnapshotsAndPreserveExactContributors()
    {
        var mapping = Build(out var q, compact: true);
        var customers = new List<Customer> { new("customer-7", "Acme") };
        var items = new List<LineItem> { new("item-1", "load-42", 2m, 50m), new("item-2", "load-42", 1m, 25m) };
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources
            .Root(q.Clr.Shape<Load>(), l => l.Id)
            .Bind(q.Clr.Shape<Customer>(), customers, c => c.Id)
            .Bind(q.Clr.Shape<LineItem>(), items, i => i.Id));
        customers.Clear();
        items.Clear();

        var outcome = await mapping.EvaluateAsync(evaluator, Loads(), l => l.Id);
        Assert.True(outcome.IsSuccessful, Explain(outcome));
        var row = Assert.Single(mapping.Mapper.Map(outcome.PhysicalExecution!).Rows);
        Assert.Equal(new Summary("load-42", "Acme", 125m), row.Value);
        Assert.Equal(new[] { "customer-7", "item-1", "item-2", "load-42" },
            row.Source.InputOccurrences.Select(x => x.ObservationIdentity!).Order(StringComparer.Ordinal));
        Assert.Equal(row.Value, Assert.Single(await mapping.MapAsync(evaluator, Loads())));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, Loads(), _ => "other-id"));
    }

    [Fact]
    public async Task PreparedCollectionsReuseAcquiredEvidenceAndStillApplyInvocationBudgets()
    {
        var mapping = Build(compact: true);
        Customer[] customers = [new("customer-7", "Acme")];
        LineItem[] items = [new("item-1", "load-42", 2m, 50m), new("item-2", "load-42", 1m, 25m)];
        var evaluator = mapping.CreateInMemoryEvaluator(customers, items, maximumRows: 4);
        var first = await mapping.EvaluateAsync(evaluator, Loads(), load => load.Id, evaluation: new("first"));
        Assert.True(first.IsSuccessful, Explain(first));
        var warmed = await mapping.EvaluateAsync(evaluator, Loads(), load => load.Id, evaluation: new("warmed"));
        Assert.True(warmed.IsSuccessful, Explain(warmed));
        Assert.Equal(2, warmed.PhysicalExecution!.SourceReads.Length);
        var root = mapping.Compilation.Plan!.InputContract.Sources.Single(source =>
            source.Role == RelationQuerySourceInputRole.RelationRoot);
        foreach (var source in first.PhysicalExecution!.Evidence!.Sources.Where(source => source.Input != root.Input.Id))
            Assert.Same(source, warmed.PhysicalExecution.Evidence!.Sources.Single(warm => warm.Input == source.Input));
        var row = Assert.Single(mapping.Mapper.Map(warmed.PhysicalExecution).Rows);
        Assert.Equal(new Summary("load-42", "Acme", 125m), row.Value);
        Assert.Equal(4, row.Source.InputOccurrences.Length);

        var overBudget = await mapping.EvaluateAsync(evaluator,
            Loads().Append(new Load("load-43", "customer-7")), load => load.Id);
        Assert.False(overBudget.IsSuccessful);
        var enumerations = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapping.MapAsync(evaluator, TooManyRoots()));
        Assert.Equal(1, enumerations);

        Assert.Equal([row.Value], await mapping.MapAsync(evaluator, Loads()));
        Assert.Empty(await mapping.MapAsync(evaluator, [new Load("other-load", "customer-7")]));

        var noRoots = await mapping.EvaluateAsync(evaluator, mapping.Evaluate(new("no-roots")).Build());
        var execution = noRoots.PhysicalExecution!;
        var evidence = execution.Evidence!;
        var plan = mapping.Compilation.Plan!;
        var ordinary = new RelationQueryRuntimeEvidence(evidence.Evaluation, plan, evidence.Completeness,
            sources: evidence.Sources, fields: evidence.Fields, capabilities: evidence.Capabilities);
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, ordinary));
        Assert.False(noRoots.IsSuccessful);
        Assert.Equal(reference.Status, execution.Status);
        Assert.Equal(reference.RequirementGapAnalysis.Gaps.Select(gap => gap.Id),
            execution.Interpretation!.RequirementGapAnalysis.Gaps.Select(gap => gap.Id));
        Assert.Equal(reference.Diagnostics.Select(diagnostic => diagnostic.Code),
            execution.Interpretation.Diagnostics.Select(diagnostic => diagnostic.Code));

        IEnumerable<Load> TooManyRoots()
        {
            enumerations++;
            yield return Loads()[0];
            yield return new Load("load-43", "customer-7");
        }
    }

    [Fact]
    public async Task FusedMappingPreservesReadBudgetForAnEmptyAcquiredSource()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>();
        var output = loads.Join(q.Source<Customer>(), (load, customer) => load.CustomerId == customer.Id,
            (load, customer) => new LoadCustomer(load.Id, customer.Name));
        var mapping = RelationDtoMapping.Create(loads, output, RelationOutputMode.ZeroOrOnePerRoot);
        var evaluator = mapping.CreateInMemoryEvaluator(
            sources => sources.Bind(Array.Empty<Customer>()), maximumRows: 1);

        var preparation = await mapping.EvaluateAsync(evaluator, Array.Empty<Load>(), load => load.Id);
        Assert.True(preparation.IsSuccessful, Explain(preparation));
        Assert.NotNull(PreparedLocalDtoKernel<LoadCustomer>.TryCompile(
            mapping.Compilation.Plan!, preparation, mapping.Mapper, nameof(Load.Id)));

        var canonical = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mapping.MapAsync(evaluator, Loads(), load => load.Id));
        var fused = await Assert.ThrowsAsync<InvalidOperationException>(() => mapping.MapAsync(evaluator, Loads()));
        Assert.Equal(canonical.Message, fused.Message);
    }

    [Fact]
    public async Task FusedOrdinalAdmissionPreservesProjectionOrderDiagnosticsAndDerivedRootFallback()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<ObservedLoad>();
        var output = loads.Join(q.Source<Customer>(), (load, customer) => load.CustomerId == customer.Id,
            (load, customer) => new Summary(load.Id, customer.Name, load.Amount));
        var mapping = RelationDtoMapping.Create(loads, output, RelationOutputMode.ZeroOrOnePerRoot);
        Customer[] customers = [new("customer-7", "Acme")];
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources.Bind(customers));

        var reference = new ObservedLoad("customer-7");
        var root = new ObservedLoad("customer-7");
        Assert.Equal(await Canonical(reference), await mapping.MapAsync(evaluator, new[] { root }));
        Assert.Equal(new[] { nameof(ObservedLoad.Id), nameof(ObservedLoad.CustomerId), nameof(ObservedLoad.Amount) }, reference.Reads);
        Assert.Equal(reference.Reads, root.Reads);

        await CompareFailure<ArgumentException>(new ObservedLoad(null), new ObservedLoad(null));
        await CompareFailure<System.Reflection.TargetInvocationException>(
            new ObservedLoad("customer-7", fail: true), new ObservedLoad("customer-7", fail: true));
        await CompareFailure<ArgumentException>(new DerivedObservedLoad(), new DerivedObservedLoad());

        ObservedLoad[] mixed = [new CompatibleObservedLoad(), new("customer-7"), new("customer-7", id: "load-10")];
        var mixedDtos = await mapping.MapAsync(evaluator, mixed);
        Assert.Equal(new[] { "load-é", "load-10", "load-42" }, mixedDtos.Select(dto => dto.Id));
        Assert.Equal(await mapping.MapAsync(evaluator, mixed, load => load.Id), mixedDtos);
        Assert.Equal(mixedDtos, await mapping.MapAsync(evaluator, mixed.Where(static _ => true)));

        var budgeted = mapping.CreateInMemoryEvaluator(sources => sources.Bind(customers), maximumRows: 1);
        var budgetedRoot = new ObservedLoad("customer-7");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapping.MapAsync(budgeted, new[] { budgetedRoot }));
        Assert.Equal(reference.Reads, budgetedRoot.Reads);

        Task<Summary[]> Canonical(ObservedLoad value) => mapping.MapAsync(evaluator, new[] { value }, _ => "load-42");

        async Task CompareFailure<TException>(ObservedLoad ordinary, ObservedLoad fused) where TException : Exception
        {
            var expected = await Assert.ThrowsAsync<TException>(() => Canonical(ordinary));
            var actual = await Assert.ThrowsAsync<TException>(() => mapping.MapAsync(evaluator, new[] { fused }));
            Assert.Equal(expected.Message, actual.Message);
            Assert.Equal(expected.InnerException?.Message, actual.InnerException?.Message);
            Assert.Equal(ordinary.Reads, fused.Reads);
        }
    }

    [Fact]
    public async Task FusedOrdinalAdmissionKeepsWarmBatchAllocationBounded()
    {
        const int count = 128;
        var mapping = Build(compact: true);
        var loads = Enumerable.Range(0, count).Select(index => new Load($"load-{index}", "customer-7")).ToArray();
        var items = loads.Select(load => new LineItem(load.Id, load.Id, 2m, 50m)).ToArray();
        var evaluator = mapping.CreateInMemoryEvaluator(new[] { new Customer("customer-7", "Acme") }, items);
        Assert.Equal(count, (await mapping.MapAsync(evaluator, loads)).Length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = mapping.MapAsync(evaluator, loads);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(count, (await result).Length);
        Assert.InRange(allocated, 0, count * 175);
    }

    [Fact]
    public async Task FusedLargeBatchMatchesCanonicalEscapedOrderAndDuplicateRejection()
    {
        var mapping = Build(compact: true);
        string[] prefixes = ["load/", "load%", "load-é", "load-"];
        var loads = Enumerable.Range(0, 2049).Reverse()
            .Select(index => new Load($"{prefixes[index % prefixes.Length]}{index}", "customer-7")).ToArray();
        var items = loads.Where((_, index) => index % 7 != 0)
            .Select((load, index) => new LineItem(load.Id, load.Id, 1m, index)).ToArray();
        var evaluator = mapping.CreateInMemoryEvaluator(new[] { new Customer("customer-7", "Acme") }, items);

        var expected = await mapping.MapAsync(evaluator, loads, static load => load.Id);
        Assert.Equal(items.Length, expected.Length);
        Assert.Equal(expected, await mapping.MapAsync(evaluator, loads));

        Load[] repeated = [.. loads, loads[0]];
        var canonical = await Assert.ThrowsAsync<ArgumentException>(
            () => mapping.MapAsync(evaluator, repeated, static load => load.Id));
        var fused = await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, repeated));
        Assert.Equal(canonical.Message, fused.Message);
    }

    [Fact]
    public async Task PreparedJoinCanFeedAScalarKeyIntoAnotherJoinAndFallsBackForDuplicateKeys()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>();
        var regionSource = q.Source<Region>();
        var output = loads
            .Join(q.Source<Customer>(), (load, customer) => load.CustomerId == customer.Id,
                (load, customer) => new { load.Id, RegionCode = customer.Name })
            .Join(regionSource, (load, region) => load.RegionCode == region.Id,
                (load, region) => new LoadCustomer(load.Id, region.Label));
        var mapping = RelationDtoMapping.Create(loads, output, RelationOutputMode.ZeroOrOnePerRoot);
        Customer[] customers = [new("customer-7", "north")];
        Region[] regions = [new("north", "load-42", "North Region")];
        var evaluator = mapping.CreateInMemoryEvaluator(customers, regions);

        var preparation = await mapping.EvaluateAsync(evaluator, Array.Empty<Load>(), static load => load.Id);
        Assert.True(preparation.IsSuccessful, Explain(preparation));
        Assert.NotNull(PreparedLocalDtoKernel<LoadCustomer>.TryCompile(
            mapping.Compilation.Plan!, preparation, mapping.Mapper, nameof(Load.Id)));
        var expected = await mapping.MapAsync(evaluator, Loads(), static load => load.Id);
        Assert.Equal(new LoadCustomer("load-42", "North Region"), Assert.Single(expected));
        Assert.Equal(expected, await mapping.MapAsync(evaluator, Loads()));

        var fanout = RelationDtoMapping.Create(loads,
            loads.Join(regionSource, (load, region) => load.Id == region.Code,
                (load, region) => new LoadCustomer(load.Id, region.Label)), RelationOutputMode.ZeroOrOnePerRoot);
        Region[] duplicates = [.. regions, new("south", "load-42", "Other Region")];
        var duplicateEvaluator = fanout.CreateInMemoryEvaluator(sources => sources.Bind(duplicates));
        var duplicatePreparation = await fanout.EvaluateAsync(duplicateEvaluator, Array.Empty<Load>(), static load => load.Id);
        Assert.True(duplicatePreparation.IsSuccessful, Explain(duplicatePreparation));
        Assert.Null(PreparedLocalDtoKernel<LoadCustomer>.TryCompile(
            fanout.Compilation.Plan!, duplicatePreparation, fanout.Mapper, nameof(Load.Id)));
        var reference = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fanout.MapAsync(duplicateEvaluator, Loads(), static load => load.Id));
        var fallback = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fanout.MapAsync(duplicateEvaluator, Loads()));
        Assert.Equal(reference.Message, fallback.Message);
    }

    sealed record Region(string Id, string Code, string Label);
    sealed record RawSummary(string Id, string CustomerName, ObservationValue Total);

    class ObservedLoad(string? customerId, bool fail = false, string id = "load-42")
    {
        internal List<string> Reads { get; } = [];

        public string Id
        {
            get
            {
                Reads.Add(nameof(Id));
                return id;
            }
        }

        public string CustomerId
        {
            get
            {
                Reads.Add(nameof(CustomerId));
                return fail ? throw new InvalidOperationException("Getter failed.") : customerId!;
            }
        }

        public decimal Amount
        {
            get
            {
                Reads.Add(nameof(Amount));
                return 125m;
            }
        }
    }

    sealed class DerivedObservedLoad() : ObservedLoad("customer-7")
    {
        public string Extra => "undeclared";
    }

    sealed class CompatibleObservedLoad() : ObservedLoad("customer-7", id: "load-é");

    [Fact]
    public async Task GroupedResultsWithInvocationParametersAreEvaluatedPerCall()
    {
        var q = RelationQuery.Expression();
        var loads = q.Source<Load>();
        var multiplier = q.Parameter<decimal>("multiplier");
        var totals = q.Source<LineItem>().SumBy(i => i.LoadId, i => i.Quantity * i.UnitPrice * multiplier.Value);
        var output = loads.Join(totals, (l, t) => l.Id == t.Key, (l, t) => new Summary(l.Id, l.CustomerId, t.Total));
        var mapping = RelationDtoMapping.Create(loads, output, RelationOutputMode.ZeroOrOnePerRoot);
        LineItem[] items = [new("item-1", "load-42", 2m, 50m), new("item-2", "load-42", 1m, 25m)];
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources.Bind(items));

        Assert.Equal(125m, Assert.Single(await Map(1m)).Total);
        Assert.Equal(250m, Assert.Single(await Map(2m)).Total);

        Task<Summary[]> Map(decimal value) => mapping.MapAsync(evaluator, mapping.Evaluate(new("parameterized-total"))
            .Supply(Loads(), l => l.Id).Set(multiplier, value).Build());
    }

    [Fact]
    public async Task SelectorFreeMappingUsesTheDeclaredFieldAndRejectsDuplicateKeys()
    {
        var author = RelationQuery.Expression();
        var roots = author.Source<Load>();
        var output = roots.Select(load => new Summary(load.Id, load.CustomerId, 0m));
        var mapping = RelationDtoMapping.Create(roots, output, RelationOutputMode.OnePerRoot);
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources
            .Root(author.Clr.Shape<Load>(), load => load.CustomerId));
        var input = new[] { new Load("same-id", "customer-1"), new Load("same-id", "customer-2") };
        var result = await mapping.MapAsync(evaluator, input);
        Assert.Equal(new[] { "customer-1", "customer-2" }, result.Select(row => row.CustomerName).Order(StringComparer.Ordinal));

        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, new[] { input[0], input[0] }));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, new[] { new Load("id", "") }));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, new[] { new Load("id", null!) }));
    }

    [Fact]
    public async Task SelectorFreeMappingChecksEvaluatorAndCancellationBeforeReadingRoots()
    {
        var mapping = Build(out var q, compact: true);
        var evaluator = mapping.CreateInMemoryEvaluator(sources => sources
            .Root(q.Clr.Shape<Load>(), l => l.Id)
            .Bind(q.Clr.Shape<Customer>(), Array.Empty<Customer>(), c => c.Id)
            .Bind(q.Clr.Shape<LineItem>(), Array.Empty<LineItem>(), i => i.Id));
        var seen = 0;
        IEnumerable<Load> Roots()
        {
            seen++;
            yield return Loads()[0];
        }
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(new UnusedEvaluator(), Roots()));
        await Assert.ThrowsAsync<ArgumentException>(() => Build(compact: true).MapAsync(evaluator, Roots()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.MapAsync(evaluator, Roots(), canceled.Token));
        Assert.Equal(0, seen);
    }

    [Fact]
    public void CollectionBindingsRejectDuplicateIdentitiesIncompleteConfigurationAndOversizedSnapshots()
    {
        var mapping = Build(out var q, compact: true);
        var customer = new Customer("customer-7", "Acme");
        Assert.Throws<ArgumentException>(() => mapping.CreateInMemoryEvaluator(sources => sources
            .Bind(q.Clr.Shape<Customer>(), new[] { customer, customer }, c => c.Id)));
        Assert.Throws<ArgumentException>(() => mapping.CreateInMemoryEvaluator(sources => sources
            .Bind(q.Clr.Shape<Customer>(), new[] { customer, new Customer("customer-8", "Other") }, c => c.Id),
            maximumRows: 1));
        Assert.Throws<InvalidOperationException>(() => mapping.CreateInMemoryEvaluator(sources => sources
            .Root(q.Clr.Shape<Load>(), l => l.Id)));
    }

    [Fact]
    public async Task DirectInvocationMatchesTheExpandedEvaluationAndDtoResult()
    {
        var mapping = Build(compact: true);
        var evaluator = Evaluator(mapping);
        var seen = 0;
        IEnumerable<Load> Roots()
        {
            foreach (var root in Loads()) { seen++; yield return root; }
        }
        Assert.Equal([new Summary("load-42", "Acme", 125m)],
            await mapping.MapAsync(evaluator, Roots(), x => x.Id));
        Assert.Equal(1, seen);
        var direct = await mapping.EvaluateAsync(evaluator, Loads(), x => x.Id, evaluation: new("direct/request-42"));
        var expanded = await mapping.EvaluateAsync(evaluator,
            mapping.Evaluate(new("direct/request-42")).Supply(Loads(), x => x.Id).Build());
        Assert.True(direct.IsSuccessful, Explain(direct));
        Assert.Equal(expanded.Evaluation.Fingerprint, direct.Evaluation.Fingerprint);
        Assert.Same(mapping.CompilationRequest, direct.Evaluation.Compilation);
        var row = Assert.Single(mapping.Mapper.Map(direct.PhysicalExecution!).Rows);
        Assert.Equal(4, row.Source.InputOccurrences.Length);
        var local = await mapping.EvaluateAsync(evaluator, Loads(), x => x.Id);
        Assert.Equal("cohesive.relations/dto-mapping/local", local.Evaluation.Evaluation.Value);
        Assert.Empty(await mapping.MapAsync(evaluator, Array.Empty<Load>(), x => x.Id));
    }

    [Fact]
    public async Task DirectInvocationCancellationDoesNotEnumerateOrAcquire()
    {
        var mapping = Build(compact: true);
        var evaluator = new UnusedEvaluator();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var seen = 0;
        IEnumerable<Load> Roots()
        {
            seen++;
            yield return Loads()[0];
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mapping.MapAsync(evaluator, Roots(), x => x.Id, cancellationToken: canceled.Token));
        Assert.Equal(0, seen);
        using var duringEnumeration = new CancellationTokenSource();
        IEnumerable<Load> CancelDuringEnumeration()
        {
            yield return Loads()[0];
            duringEnumeration.Cancel();
            yield return new("second", "customer-7");
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.MapAsync(evaluator,
            CancelDuringEnumeration(), x => x.Id, cancellationToken: duringEnumeration.Token));
        Assert.Equal(0, evaluator.Calls);
    }

    [Fact]
    public async Task DirectInvocationPreservesIdentityAndRootValidationBeforeAcquisition()
    {
        var mapping = Build(compact: true);
        var evaluator = new UnusedEvaluator();
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapAsync<Load>(evaluator, null!, x => x.Id));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapAsync(evaluator, Loads(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapAsync(null!, Loads(), x => x.Id));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapAsync(evaluator, new Load[] { null! }, x => x.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, Loads().Concat(Loads()), x => x.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator, Loads(), _ => ""));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapAsync(evaluator,
            new[] { new Load("invalid", null!) }, x => x.Id));
        Assert.Equal(0, evaluator.Calls);
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

    static RelationDtoMapping<Summary> Build(bool compact, RelationOutputMode mode = RelationOutputMode.ZeroOrOnePerRoot) =>
        Build(out _, compact, mode);

    static RelationDtoMapping<Summary> Build(out RelationQueryExpressionAuthoring q, bool compact,
        RelationOutputMode mode = RelationOutputMode.ZeroOrOnePerRoot)
    {
        q = RelationQuery.Expression();
        var loads = q.Source<Load>(); var items = q.Source<LineItem>(); var customers = q.Source<Customer>();
        var totals = compact
            ? items.SumBy(i => i.LoadId, i => i.Quantity * i.UnitPrice,
                (loadId, total) => new LoadTotal(loadId, total))
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

    sealed class UnusedEvaluator : IRelationQueryEvaluator
    {
        public int Calls { get; private set; }
        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Invalid or canceled input must fail before evaluator invocation.");
        }
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
