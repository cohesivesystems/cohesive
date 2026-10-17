using BenchmarkDotNet.Running;

if (args is ["--graph-counters"])
{
    Console.WriteLine("fields,operation,median_allocated_bytes");
    foreach (var fields in new[] { 1, 128, 1024 })
    {
        var benchmark = new CompiledGraphProjectionBenchmarks { Fields = fields };
        benchmark.Setup();
        Measure("project", () => benchmark.Project());
        Measure("decode", () => benchmark.DecodeFresh());
        void Measure<T>(string operation, Func<T> run)
        {
            for (var warm = 0; warm < 8; warm++) run();
            var samples = new long[7];
            for (var i = 0; i < samples.Length; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = run();
                samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
                GC.KeepAlive(result);
            }
            Array.Sort(samples);
            Console.WriteLine($"{fields},{operation},{samples[3]}");
        }
    }
    return;
}


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
    Console.WriteLine("rows,name_length,unique_names,median_allocated_bytes");
    foreach (var rows in new[] { 1, 1024 })
    foreach (var length in new[] { 8, 256 })
    foreach (var unique in new[] { false, true })
    {
        var benchmark = new PropertyNameProjectionBenchmarks { Rows = rows, NameLength = length, UniqueNames = unique };
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
        Console.WriteLine($"{rows},{length},{unique},{samples[3]}");
        benchmark.Cleanup();
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(DeclaredProjectionBenchmarks).Assembly).Run(args);
