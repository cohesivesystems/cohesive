using BenchmarkDotNet.Running;

if (args is ["--nested-layout-profile", .. var profileArgs]) return Cohesive.Relations.Benchmarks.NestedLayoutProfile.Run(profileArgs);
if (args is ["--nested-layout-conformance"]) return Cohesive.Relations.Benchmarks.ObservationNestedLayoutBenchmarks.RunConformance();
if (args is ["--nested-layout-retained", .. var nestedArgs]) return Cohesive.Relations.Benchmarks.ObservationNestedLayoutBenchmarks.RunRetained(nestedArgs);


if (args is ["--shared-layout-retained", .. var sharedRetainedArgs])
    return Cohesive.Relations.Benchmarks.ObservationSharedLayoutBenchmarks.RunRetained(sharedRetainedArgs);

if (args is ["--field-storage-retained", .. var retainedArgs])
    return Cohesive.Relations.Benchmarks.ObservationJsonFieldStorageBenchmarks.RunRetained(retainedArgs);

if (args is ["--json-parse-profile", .. var parseArgs])
    return Cohesive.Relations.Benchmarks.ObservationJsonParseProfile.Run(parseArgs);

if (args is ["--checkpoint-wire-sizes"])
{
    Console.WriteLine("workload,utf8_bytes,sha256");
    foreach (var workload in new[] { "flat", "nested", "collection", "large" })
    {
        var benchmark = new Cohesive.Relations.Benchmarks.ObservationCheckpointBenchmarks { Workload = workload };
        benchmark.Setup();
        Console.WriteLine($"{workload},{benchmark.WireBytes},{benchmark.WireHash}");
    }
    return 0;
}

if (args is ["--portable-field-concurrency", .. var fieldArgs])
    return Cohesive.Relations.Benchmarks.PortableFieldConcurrencyBenchmark.Run(fieldArgs);

if (args is ["--hash-projection-allocations", .. var hashArgs])
    return Cohesive.Relations.Benchmarks.HashProjectionAllocationBenchmark.Run(hashArgs);
if (args is ["--prepared-result-allocations", .. var resultArgs])
    return Cohesive.Relations.Benchmarks.PreparedResultAllocationBenchmark.Run(resultArgs);

if (args is ["--field-assignment-allocations", .. var assignmentArgs])
    return Cohesive.Relations.Benchmarks.FieldAssignmentAllocationBenchmark.Run(assignmentArgs);

if (args is ["--entity-transition-allocations", .. var transitionArgs])
    return Cohesive.Relations.Benchmarks.EntityTransitionAllocationBenchmark.Run(transitionArgs);

if (args is ["--strict-read-allocations", .. var strictArgs])
    return Cohesive.Relations.Benchmarks.StrictReadAllocationBenchmark.Run(strictArgs);

if (args is ["--query-identity-digests"])
{
    Console.WriteLine("maximum_local_rows,realization,placement,physical");
    foreach (var rows in new[] { 32, 1024 })
    {
        var benchmark = new Cohesive.Relations.Benchmarks.RelationQueryPhysicalPlanningBenchmarks { MaximumLocalRows = rows };
        benchmark.Setup();
        var plan = benchmark.PhysicalPlanning().Plan ?? throw new InvalidOperationException("Invalid fixture.");
        Console.WriteLine($"{rows},{plan.Realization.Value},{plan.Placement.Fingerprint.Value},{plan.Fingerprint.Value}");
    }
    return 0;
}

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
var discovered = false;
foreach (var summary in summaries)
{
    discovered = true;
    if (summary.HasCriticalValidationErrors
        || summary.Reports.Any(static report => !report.Success))
    {
        return 1;
    }
}

var informational = IsInformationalInvocation(args);
return discovered || informational ? 0 : 1;

static bool IsInformationalInvocation(IReadOnlyList<string> arguments)
{
    var informational = false;
    for (var index = 0; index < arguments.Count; index++)
    {
        var argument = arguments[index];
        if (argument is "--help" or "--info" or "--version")
        {
            informational = true;
            continue;
        }

        if (argument.Equals("--list", StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= arguments.Count || !IsListFormat(arguments[index]))
                return false;
            informational = true;
            continue;
        }

        const string ListPrefix = "--list=";
        if (argument.StartsWith(ListPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsListFormat(argument[ListPrefix.Length..]))
                return false;
            informational = true;
        }
    }

    return informational;
}

static bool IsListFormat(string value) =>
    value.Equals("flat", StringComparison.OrdinalIgnoreCase)
    || value.Equals("tree", StringComparison.OrdinalIgnoreCase);
