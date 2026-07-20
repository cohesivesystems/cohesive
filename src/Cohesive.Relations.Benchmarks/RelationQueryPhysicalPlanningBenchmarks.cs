using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Physical planning for a federated Load, Customer, and Equipment relation.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationQueryPhysicalPlanningBenchmarks
{
    const int BatchSize = 32;

    CompiledRelationQueryPlan plan = null!;
    RelationQueryRealizationReport realization = null!;
    RelationQuerySourcePlacement placement = null!;
    RelationQueryPhysicalPlanningPolicy policy = null!;

    /// <summary>Maximum number of rows permitted for bounded local execution.</summary>
    [Params(32, 1024)]
    public int MaximumLocalRows { get; set; }

    /// <summary>Builds the static plan, contextual realization, placement, and policy outside the measured method.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: BatchSize,
            maximumLocalRows: MaximumLocalRows,
            maximumReferenceKeysPerObservation: 1,
            customerMaximumBufferedRows: MaximumLocalRows);
        plan = compilation.Plan;
        realization = compilation.Realization;
        placement = compilation.Placement;
        policy = FederatedLoadPhysicalExecutionFixture.CreatePolicy(
            maximumBatchSize: BatchSize,
            maximumLocalRows: MaximumLocalRows,
            maximumReferenceKeysPerObservation: 1);
    }

    /// <summary>Compiles a physical plan from already-static semantic and contextual inputs.</summary>
    /// <returns>The physical planning result, including its deterministic plan and diagnostics.</returns>
    [Benchmark]
    [BenchmarkCategory("Physical", "Planning")]
    public RelationQueryPhysicalPlanningResult PhysicalPlanning() =>
        RelationQueryPhysicalPlanner.Compile(plan, realization, placement, policy);
}
