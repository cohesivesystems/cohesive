using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Isolated sustained fallback loops for CPU samples and matching JIT code.</summary>
public static class NestedLayoutProfile
{
    /// <summary>Warms one route before its marked sustained loop.</summary>
    /// <param name="args">Route existing/shared/forced/probe/separated/density and duration from 1 through 30 seconds.</param>
    /// <returns>Zero with operation/allocation counters, or two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">Fixture equivalence fails.</exception>
    public static int Run(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("existing" or "shared" or "forced" or "probe" or "separated" or "density")
            || !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 30) return 2;
        var benchmark = new ObservationNestedLayoutBenchmarks { Width = 16, Count = 128, Scenario = "case-array" };
        benchmark.Setup();
        Func<ObservationValue> read = args[0] switch
        {
            "existing" => benchmark.ParseExisting,
            "shared" => benchmark.ParseShared,
            "forced" => benchmark.ParseForcedFallback,
            "separated" => benchmark.ParseSharedSeparated,
            "density" => benchmark.ParseDensityAware,
            _ => () => ObservationValue.FromBool(benchmark.ProbeShared())
        };
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed < TimeSpan.FromSeconds(2)) _ = read();
        var (operations, checksum, elapsed, allocated) = Measure(read, TimeSpan.FromSeconds(seconds));
        Console.WriteLine(FormattableString.Invariant($"route={args[0]},operations={operations},checksum={checksum},elapsed_ns={elapsed:F0},allocated_bytes={allocated}"));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (long Operations, long Checksum, double Elapsed, long Allocated) Measure(Func<ObservationValue> read, TimeSpan duration)
    {
        long operations = 0, checksum = 0;
        var timer = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        do
        {
            for (var index = 0; index < 32; index++) checksum += (int)read().Kind;
            operations += 32;
        } while (timer.Elapsed < duration);
        return (operations, checksum, timer.Elapsed.TotalNanoseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
