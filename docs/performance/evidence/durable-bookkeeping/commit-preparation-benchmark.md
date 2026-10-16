```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                      | Rows | Mean            | Error           | StdDev       | Ratio | Gen0    | Allocated | Alloc Ratio |
|---------------------------- |----- |----------------:|----------------:|-------------:|------:|--------:|----------:|------------:|
| **ConstructAndHashWholeCommit** | **1024** |  **3,040,312.2 ns** |    **94,480.06 ns** |  **5,178.77 ns** | **1.000** |  **3.9063** |  **49.64 KB** |        **1.00** |
| ConstructCommit             | 1024 |        898.3 ns |       120.50 ns |      6.60 ns | 0.000 |  0.1869 |   1.53 KB |        0.03 |
|                             |      |                 |                 |              |       |         |           |             |
| **ConstructAndHashWholeCommit** | **8192** | **24,508,705.3 ns** | **1,217,708.60 ns** | **66,746.72 ns** | **1.000** | **31.2500** | **497.96 KB** |       **1.000** |
| ConstructCommit             | 8192 |        916.0 ns |        72.65 ns |      3.98 ns | 0.000 |  0.1869 |   1.53 KB |       0.003 |
