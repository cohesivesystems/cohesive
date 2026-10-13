using System.Globalization;

namespace Cohesive.Relations.Benchmarks;

// Opt-in allocation qualification; factories own their results and inputs are prepared outside measurement.
internal static class AllocationMeasurement
{
    internal static int Iterations(string[] args)
    {
        var count = args.Length == 0 ? 32 : int.Parse(args[0], CultureInfo.InvariantCulture);
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(args));
        return count;
    }
    internal static void Header() => Console.WriteLine("workload,implementation,median_bytes_per_operation");
    internal static void Measure(string name, string implementation, Func<object?> operation, int count)
    {
        for (var warm = 0; warm < 20; warm++) GC.KeepAlive(operation());
        var samples = new double[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < count; iteration++) GC.KeepAlive(operation());
            samples[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;
        }
        Array.Sort(samples);
        Console.WriteLine($"{name},{implementation},{samples[3].ToString(CultureInfo.InvariantCulture)}");
    }
}
