using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Runs the existing parser benchmark in a sustained loop for CPU and JIT inspection.</summary>
public static class ObservationJsonParseProfile
{
    /// <summary>Warms the parser and runs an isolated measured loop without BenchmarkDotNet orchestration.</summary>
    /// <param name="args">Workload name, duration in seconds from 1 through 60, and an optional warmup workload name.</param>
    /// <returns>Zero after printing operation count and a consumed result checksum; two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">The benchmark fixture fails parser equivalence or result checks.</exception>
    public static int Run(string[] args)
    {
        if (args.Length is not (2 or 3) || !IsWorkload(args[0])
            || (args.Length == 3 && !IsWorkload(args[2]))
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 1 or > 60)
        {
            Console.Error.WriteLine("--json-parse-profile <flat|nested|collection|large> <seconds:1..60> [warmup-workload]");
            return 2;
        }
        var warmupWorkload = args.Length == 3 ? args[2] : args[0];
        var warmupBenchmark = new ObservationValuePipelineBenchmarks { Workload = warmupWorkload };
        warmupBenchmark.Setup();
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed < TimeSpan.FromSeconds(1)) _ = warmupBenchmark.ParseDirect();
        var benchmark = new ObservationValuePipelineBenchmarks { Workload = args[0] };
        benchmark.Setup();
        var result = Measure(benchmark, TimeSpan.FromSeconds(seconds));
        Console.WriteLine(FormattableString.Invariant($"workload={args[0]},warmup={warmupWorkload},operations={result.Operations},elapsed_ns={result.ElapsedNanoseconds:F0},allocated_bytes={result.AllocatedBytes},checksum={result.Checksum}"));
        return 0;
    }

    static bool IsWorkload(string name) => name is "flat" or "nested" or "collection" or "large";

    // Keep this marker in sampled stacks so analysis excludes setup and warmup.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (long Operations, long Checksum, double ElapsedNanoseconds, long AllocatedBytes) Measure(ObservationValuePipelineBenchmarks benchmark, TimeSpan duration)
    {
        const int BatchSize = 1024;
        long operations = 0, checksum = 0;
        var clock = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        do
        {
            for (var index = 0; index < BatchSize; index++) checksum += (int)benchmark.ParseDirect().Kind;
            operations += BatchSize;
        } while (clock.Elapsed < duration);
        var elapsed = clock.Elapsed.TotalNanoseconds;
        return (operations, checksum, elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
