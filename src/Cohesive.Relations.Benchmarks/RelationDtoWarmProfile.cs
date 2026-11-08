using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Cohesive.Relations.Benchmarks;

// Opt-in CPU/JIT attribution over the exact warm benchmark fixture, outside BenchmarkDotNet timing.
internal static class RelationDtoWarmProfile
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 60)
            throw new ArgumentException("Expected kernel-simple, kernel-joined, auto-simple or auto-joined and 1–60 seconds.");
        var benchmark = new RelationDtoWarmBenchmarks { RowCount = 1024 };
        benchmark.Setup();
        Func<object> run = args[0] switch
        {
            "kernel-simple" => () => benchmark.CompiledKernelOnlySimple(),
            "kernel-joined" => () => benchmark.CompiledKernelOnlyJoined(),
            "auto-simple" => () => benchmark.AutoMapperCanonicalRowsSimple(),
            "auto-joined" => () => benchmark.AutoMapperCanonicalRowsJoined(),
            _ => throw new ArgumentException("Unknown profile route.")
        };
        for (var index = 0; index < 256; index++) GC.KeepAlive(run());
        Console.WriteLine($"PROFILE_START {args[0]}");
        var operations = Measure(run, TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"PROFILE_END operations={operations}");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Measure(Func<object> run, TimeSpan duration)
    {
        var started = Stopwatch.GetTimestamp();
        long count = 0;
        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            GC.KeepAlive(run());
            count++;
        }
        return count;
    }
}
