using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(DeclaredProjectionBenchmarks).Assembly).Run(args);
