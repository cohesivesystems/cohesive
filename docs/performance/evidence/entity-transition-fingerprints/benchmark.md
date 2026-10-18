```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method            | PayloadBytes | Mean         | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------ |------------- |-------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **EagerFingerprints** | **32**           |    **78.206 μs** | **40.7358 μs** | **2.2329 μs** |  **1.00** |    **0.03** | **1.4648** |  **13.25 KB** |        **1.00** |
| FencedFreshCommit | 32           |     5.322 μs |  0.2531 μs | 0.0139 μs |  0.07 |    0.00 | 0.3052 |   2.56 KB |        0.19 |
|                   |              |              |            |           |       |         |        |           |             |
| **EagerFingerprints** | **16384**        |   **101.597 μs** | **83.6865 μs** | **4.5871 μs** |  **1.00** |    **0.06** | **1.4648** |  **13.15 KB** |        **1.00** |
| FencedFreshCommit | 16384        |     5.325 μs |  0.2765 μs | 0.0152 μs |  0.05 |    0.00 | 0.3052 |   2.56 KB |        0.19 |
|                   |              |              |            |           |       |         |        |           |             |
| **EagerFingerprints** | **1048576**      | **1,161.227 μs** | **43.5303 μs** | **2.3860 μs** | **1.000** |    **0.00** |      **-** |  **13.13 KB** |        **1.00** |
| FencedFreshCommit | 1048576      |     5.508 μs |  4.0570 μs | 0.2224 μs | 0.005 |    0.00 | 0.3052 |   2.56 KB |        0.20 |
