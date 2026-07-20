using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Benchmarks;

/// <summary>
/// Bounded relationship-key extraction, deduplication, batched acquisition, and correlation over a federated relation.
/// </summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RelationQueryPhysicalExecutionBenchmarks
{
    const int BatchSize = 32;

    RelationQueryPhysicalExecutor executor = null!;
    RelationQueryPhysicalExecutionRequest request = null!;

    /// <summary>Number of supplied Load roots correlated during each execution.</summary>
    [Params(32, 1024)]
    public int RootCount { get; set; }

    /// <summary>Builds and validates a bounded federated execution scenario outside the measured method.</summary>
    /// <returns>A task that completes after validating batching, deduplication, ordering, and mapped output.</returns>
    /// <exception cref="InvalidOperationException">The shared fixture violates an expected execution invariant.</exception>
    [GlobalSetup]
    public async Task Setup()
    {
        var distinctCustomerCount = Math.Max(1, RootCount / 2);
        var distinctEquipmentCount = Math.Max(1, RootCount / 4);
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RequiredRelationDocument,
            maximumBatchSize: BatchSize,
            maximumLocalRows: checked(RootCount * 4),
            maximumReferenceKeysPerObservation: 1,
            customerMaximumBufferedRows: RootCount,
            maximumBufferedRows: RootCount);
        var validation = FederatedLoadConformanceData.CreatePhysicalScenario(
            compilation,
            RootCount,
            distinctCustomerCount,
            distinctEquipmentCount);
        var validationResult = await new RelationQueryPhysicalExecutor(validation.Readers).ExecuteAsync(
            CreateRequest(compilation, validation.SuppliedLoads, "benchmarks/federated/physical-validation"));
        Validate(
            compilation,
            validation,
            validationResult,
            distinctCustomerCount,
            distinctEquipmentCount);

        var measured = FederatedLoadConformanceData.CreatePhysicalScenario(
            compilation,
            RootCount,
            distinctCustomerCount,
            distinctEquipmentCount,
            recordRequests: false);
        executor = new(measured.Readers);
        request = CreateRequest(
            compilation,
            measured.SuppliedLoads,
            "benchmarks/federated/physical-execution");
    }

    /// <summary>Executes bounded, deduplicated, batched Customer and Equipment acquisition and local correlation.</summary>
    /// <returns>The canonical physical execution result and its interpreted relation output.</returns>
    [Benchmark]
    [BenchmarkCategory("Physical", "Execution")]
    public ValueTask<RelationQueryPhysicalExecutionResult> ExecuteBatchedFederatedRelation() =>
        executor.ExecuteAsync(request);

    static RelationQueryPhysicalExecutionRequest CreateRequest(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        RelationQuerySuppliedSourceInput suppliedLoads,
        string evaluation) => new(
        compilation.Plan,
        compilation.PhysicalPlan,
        compilation.Realization,
        new(evaluation),
        suppliedSources: [suppliedLoads],
        capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan));

    static void Validate(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        FederatedLoadConformanceData.PhysicalScenario scenario,
        RelationQueryPhysicalExecutionResult result,
        int distinctCustomerCount,
        int distinctEquipmentCount)
    {
        if (result.Status != RelationQueryExecutionStatus.Succeeded || !result.Diagnostics.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        ValidateBatches(scenario.Customers.Requests, distinctCustomerCount);
        ValidateBatches(scenario.Equipment.Requests, distinctEquipmentCount);
        var expectedReads = BatchCount(distinctCustomerCount) + BatchCount(distinctEquipmentCount);
        if (result.SourceReads.Length != expectedReads)
        {
            throw new InvalidOperationException(
                $"Expected {expectedReads} deduplicated related-source reads but observed {result.SourceReads.Length}.");
        }

        var mapper = RelationDtoBenchmarkSupport.CompileMapper<FederatedLoadSearchRow>(compilation.Plan);
        var mapped = mapper.Map(result, RelationDtoMappingFailurePolicy.CollectDiagnostics);
        var mismatch = mapped.Rows.Length == scenario.Expected.Length ? -1 : 0;
        for (var index = 0; mismatch < 0 && index < mapped.Rows.Length; index++)
        {
            if (mapped.Rows[index].Value != scenario.Expected[index])
                mismatch = index;
        }
        if (!mapped.IsSuccessful || mismatch >= 0)
        {
            var difference = mismatch >= 0 && mismatch < mapped.Rows.Length && mismatch < scenario.Expected.Length
                ? $" First difference at {mismatch}: expected '{scenario.Expected[mismatch]}', actual '{mapped.Rows[mismatch].Value}'."
                : string.Empty;
            throw new InvalidOperationException(
                $"The bounded physical benchmark did not preserve canonical mapped output. "
                + $"Expected {scenario.Expected.Length} rows, observed {mapped.Rows.Length}, status {mapped.Status}."
                + difference
                + (mapped.Diagnostics.IsDefaultOrEmpty
                    ? string.Empty
                    : $" Diagnostics: {string.Join(" | ", mapped.Diagnostics.Select(static diagnostic => diagnostic.Message))}"));
        }
    }

    static void ValidateBatches(
        ImmutableArray<RelationQuerySourceReadRequest> requests,
        int expectedDistinctKeys)
    {
        if (requests.Length != BatchCount(expectedDistinctKeys))
        {
            throw new InvalidOperationException(
                $"Expected {BatchCount(expectedDistinctKeys)} batches but observed {requests.Length}.");
        }

        var keys = requests
            .Select(static request => request.Constraint)
            .OfType<RelationQueryIdentityBatchLookup>()
            .SelectMany(static lookup => lookup.Identities)
            .ToArray();
        if (keys.Length != expectedDistinctKeys
            || keys.Distinct(StringComparer.Ordinal).Count() != expectedDistinctKeys
            || requests.Any(static request =>
                request.Constraint is not RelationQueryIdentityBatchLookup lookup
                || lookup.Identities.IsDefaultOrEmpty
                || lookup.Identities.Length > BatchSize))
        {
            throw new InvalidOperationException(
                $"Physical acquisition did not issue exactly {expectedDistinctKeys} unique keys in bounded batches.");
        }
    }

    static int BatchCount(int keyCount) => (keyCount + BatchSize - 1) / BatchSize;
}
