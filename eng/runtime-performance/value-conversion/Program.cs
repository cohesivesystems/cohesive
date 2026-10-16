using BenchmarkDotNet.Running;

if (args is ["--allocation-counters"])
{
    Console.WriteLine("rows,median_allocated_bytes");
    foreach (var rows in new[] { 1, 1024 })
    {
        var benchmark = new HostedAdapterDecodeBenchmarks { Rows = rows };
        benchmark.Setup();
        for (var warm = 0; warm < 4; warm++) benchmark.Decode();
        var samples = new long[7];
        for (var index = 0; index < samples.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = benchmark.Decode();
            samples[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Console.WriteLine($"{rows},{samples[3]}");
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(DeclaredProjectionBenchmarks).Assembly).Run(args);
