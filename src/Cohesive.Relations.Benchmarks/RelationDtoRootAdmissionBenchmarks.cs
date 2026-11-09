using AutoMapper;
using BenchmarkDotNet.Attributes;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Mapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures a load summary that joins customers and sums line items, including CLR root admission.</summary>
/// <remarks>Leaves garbage collection to the runtime so pooled root buffers age as they do in repeated mapping.</remarks>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[GcForce(false)]
public class RelationDtoRootAdmissionBenchmarks
{
    Load[] loads = null!;
    Customer[] customers = null!;
    LineItem[] items = null!;
    RelationDtoMapping<LoadSummary> mapping = null!;
    IRelationQueryEvaluator prepared = null!;
    IRelationQueryEvaluator firstCall = null!;
    IMapper mapper = null!;
    Dictionary<string, Customer> customersById = null!;
    Dictionary<string, decimal> totalsById = null!;

    /// <summary>Gets or sets the batch size, with two line items and one customer per load.</summary>
    [Params(1, 128, 1024, 8192)]
    public int LoadCount { get; set; }

    /// <summary>Builds both configurations and verifies equivalent load summaries before timing.</summary>
    /// <returns>A task that completes after the outputs have been verified.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        var ids = Enumerable.Range(1, LoadCount);
        loads = [.. ids.Select(i => new Load($"load-{i}", $"customer-{i}"))];
        customers = [.. ids.Select(i => new Customer($"customer-{i}", $"Customer {i}"))];
        items = [.. ids.SelectMany(i => new[] {
            new LineItem($"item-{i}-1", $"load-{i}", 2m, 50m),
            new LineItem($"item-{i}-2", $"load-{i}", 1m, 25m)
        })];

        var query = RelationQuery.Expression();
        var roots = query.Source<Load>();
        var output = roots
            .Join(query.Source<LineItem>().SumBy(i => i.LoadId, i => i.Quantity * i.UnitPrice),
                (load, total) => load.Id == total.Key, (load, total) => new { load.Id, load.CustomerId, total.Total })
            .Join(query.Source<Customer>(), (load, customer) => load.CustomerId == customer.Id,
                (load, customer) => new LoadSummary(load.Id, customer.Name, load.Total));
        mapping = RelationDtoMapping.Create(roots, output, RelationOutputMode.ZeroOrOnePerRoot);
        prepared = ConfigureEvaluator();

        var configuration = new MapperConfiguration(
            cfg => cfg.CreateMap<JoinedLoad, LoadSummary>(), NullLoggerFactory.Instance);
        configuration.AssertConfigurationIsValid();
        configuration.CompileMappings();
        mapper = configuration.CreateMapper();
        customersById = customers.ToDictionary(customer => customer.Id, StringComparer.Ordinal);
        totalsById = items.GroupBy(item => item.LoadId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity * item.UnitPrice), StringComparer.Ordinal);

        var reference = await mapping.MapAsync(prepared, loads, static load => load.Id).ConfigureAwait(false);
        var warm = await CohesiveInMemory().ConfigureAwait(false);
        var expected = LinqAndAutoMapper().OrderBy(row => row.Id, StringComparer.Ordinal);
        if (reference.Length != LoadCount || reference.Any(row => row.Total != 125m)
            || !reference.AsSpan().SequenceEqual(warm)
            || !reference.SequenceEqual(expected)
            || !reference.SequenceEqual(PreparedLookupsAndAutoMapper().OrderBy(row => row.Id, StringComparer.Ordinal)))
            throw new InvalidOperationException("The load-summary implementations returned different values.");
    }

    /// <summary>Groups, joins and maps from the original CLR collections with AutoMapper.</summary>
    /// <returns>The complete load summaries in input order.</returns>
    [Benchmark]
    public LoadSummary[] LinqAndAutoMapper()
    {
        var totals = items.GroupBy(item => item.LoadId)
            .Select(group => new { LoadId = group.Key, Total = group.Sum(item => item.Quantity * item.UnitPrice) });
        var rows =
            from load in loads
            join total in totals on load.Id equals total.LoadId
            join customer in customers on load.CustomerId equals customer.Id
            select new JoinedLoad(load.Id, customer, total.Total);
        return mapper.Map<LoadSummary[]>(rows);
    }

    /// <summary>Uses prebuilt customer and total lookups and maps CLR inputs with AutoMapper.</summary>
    /// <returns>The matching load summaries in input order.</returns>
    [Benchmark]
    public LoadSummary[] PreparedLookupsAndAutoMapper()
    {
        var result = new LoadSummary[loads.Length];
        var count = 0;
        foreach (var load in loads)
            if (totalsById.TryGetValue(load.Id, out var total) && customersById.TryGetValue(load.CustomerId, out var customer))
                result[count++] = mapper.Map<JoinedLoad, LoadSummary>(new(load.Id, customer, total));
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    /// <summary>Admits the original CLR roots and maps using a warmed Cohesive evaluator.</summary>
    /// <returns>The matching load summaries in canonical identity order.</returns>
    [Benchmark]
    public Task<LoadSummary[]> CohesiveInMemory() => mapping.MapAsync(prepared, loads);

    /// <summary>Builds a fresh evaluator and snapshots its related sources.</summary>
    /// <returns>The configured evaluator before its first invocation.</returns>
    [Benchmark]
    public IRelationQueryEvaluator ConfigureEvaluator() =>
        mapping.CreateInMemoryEvaluator(customers, items, maximumRows: Math.Max(4096, 4 * LoadCount));

    /// <summary>Creates a fresh evaluator outside the timing of each first invocation.</summary>
    [IterationSetup(Target = nameof(FirstCohesiveMap))]
    public void CreateFirstCallEvaluator() => firstCall = ConfigureEvaluator();

    /// <summary>Prepares and invokes the mapper against a fresh evaluator in a warmed process.</summary>
    /// <returns>The matching load summaries in canonical identity order.</returns>
    [Benchmark]
    public Task<LoadSummary[]> FirstCohesiveMap() => mapping.MapAsync(firstCall, loads);

    /// <summary>A load summary with its ID, customer name, and summed line-item amount.</summary>
    /// <param name="Id">Load identity.</param>
    /// <param name="CustomerName">Name of the joined customer.</param>
    /// <param name="Total">Sum of each line item's quantity multiplied by its unit price.</param>
    public sealed record LoadSummary(string Id, string CustomerName, decimal Total);

    record Load(string Id, string CustomerId);
    record Customer(string Id, string Name);
    record LineItem(string Id, string LoadId, decimal Quantity, decimal UnitPrice);
    record JoinedLoad(string Id, Customer Customer, decimal Total);
}
