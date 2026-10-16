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

if (args is ["--annotation-counters"])
{
    Console.WriteLine("string_length,median_allocated_bytes");
    foreach (var length in new[] { 32, 1024 })
    {
        var benchmark = new NativeStringAnnotationBenchmarks { Length = length };
        benchmark.Setup();
        for (var warm = 0; warm < 20; warm++) benchmark.CreateAndWrite();
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++) benchmark.CreateAndWrite();
            samples[run] = (GC.GetAllocatedBytesForCurrentThread() - before) / 128;
        }
        Array.Sort(samples);
        Console.WriteLine($"{length},{samples[3]}");
        benchmark.Cleanup();
    }
    return;
}

if (args is ["--property-name-counters"])
{
    Console.WriteLine("rows,name_length,median_allocated_bytes");
    foreach (var rows in new[] { 1, 1024 })
    foreach (var length in new[] { 8, 256 })
    {
        var benchmark = new PropertyNameProjectionBenchmarks { Rows = rows, NameLength = length };
        benchmark.Setup();
        for (var warm = 0; warm < 20; warm++) benchmark.Project();
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = benchmark.Project();
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Console.WriteLine($"{rows},{length},{samples[3]}");
        benchmark.Cleanup();
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(DeclaredProjectionBenchmarks).Assembly).Run(args);
