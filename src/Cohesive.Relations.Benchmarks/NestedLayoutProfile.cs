using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Isolated sustained fallback loops for CPU samples and matching JIT code.</summary>
public static class NestedLayoutProfile
{
    /// <summary>Warms one route before its marked sustained loop.</summary>
    /// <param name="args">Route, duration from 1 through 30 seconds, optionally width, rows, scenario and present percentage.</param>
    /// <returns>Zero with operation/allocation counters, or two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">Fixture equivalence fails.</exception>
    public static int Run(string[] args)
    {
        if (args.Length is not (2 or 6) || args[0] is not ("existing" or "shared" or "forced" or "probe" or "separated" or "density" or "validate-packed" or "validate-fixed" or "parse-packed" or "validate-bound-packed" or "validate-bound-fixed" or "parse-bound-packed")
            || !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 30) return 2;
        var benchmark = new ObservationNestedLayoutBenchmarks { Width = 16, Count = 128, Scenario = "case-array" };
        if (args.Length == 6)
        {
            if (!int.TryParse(args[2], out var width) || width is < 1 or > 256
                || !int.TryParse(args[3], out var rows) || rows is < 1 or > 4096
                || !int.TryParse(args[5], out var percent) || percent is < 1 or > 100
                || args[4] is not ("array" or "nested" or "named-array" or "record" or "case-array")) return 2;
            benchmark.Width = width; benchmark.Count = rows; benchmark.Scenario = args[4]; benchmark.PresentPercent = percent;
        }
        benchmark.Setup();
        Func<ObservationValue> read = args[0] switch
        {
            "existing" => benchmark.ParseExisting,
            "shared" => benchmark.ParseShared,
            "forced" => benchmark.ParseForcedFallback,
            "separated" => benchmark.ParseSharedSeparated,
            "density" => benchmark.ParseDensityAware,
            "parse-packed" => benchmark.ParseCompactOrdinal,
            "parse-bound-packed" => benchmark.ParseBoundCompactOrdinal,
            "validate-bound-packed" => () => ObservationValue.FromBool(benchmark.ValidateBoundCompactOrdinal()),
            "validate-bound-fixed" => () => ObservationValue.FromBool(benchmark.ValidateBoundShared()),
            "validate-packed" => () => ObservationValue.FromBool(benchmark.ValidateCompactOrdinal()),
            "validate-fixed" => () => ObservationValue.FromBool(benchmark.ValidateShared()),
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
