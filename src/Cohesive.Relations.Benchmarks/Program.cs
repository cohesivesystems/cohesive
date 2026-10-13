using BenchmarkDotNet.Running;

if (args is ["--portable-field-concurrency", .. var fieldArgs])
    return Cohesive.Relations.Benchmarks.PortableFieldConcurrencyBenchmark.Run(fieldArgs);

if (args is ["--hash-projection-allocations", .. var hashArgs])
    return Cohesive.Relations.Benchmarks.HashProjectionAllocationBenchmark.Run(hashArgs);
if (args is ["--prepared-result-allocations", .. var resultArgs])
    return Cohesive.Relations.Benchmarks.PreparedResultAllocationBenchmark.Run(resultArgs);

if (args is ["--field-assignment-allocations", .. var assignmentArgs])
    return Cohesive.Relations.Benchmarks.FieldAssignmentAllocationBenchmark.Run(assignmentArgs);

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
