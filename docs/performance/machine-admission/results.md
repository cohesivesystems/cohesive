# Actual-revision benchmark reports

Before: `536fde87280fde36f1005a5b564816970d894609`. After: production source committed with this report.
Both runs use the current `MachineAdmissionBenchmarks.cs` fixture, sequentially on the same host.


## Before — guarded

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method           | Edges | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|----------------- |------ |---------:|----------:|----------:|-------:|-------:|----------:|
| **GuardedAdmission** | **1**     | **3.841 μs** | **0.4742 μs** | **0.0260 μs** | **0.8965** | **0.0114** |   **7.34 KB** |
| **GuardedAdmission** | **128**   | **4.248 μs** | **1.2427 μs** | **0.0681 μs** | **0.8926** | **0.0076** |   **7.34 KB** |
| **GuardedAdmission** | **1024**  | **5.989 μs** | **0.4544 μs** | **0.0249 μs** | **0.8926** | **0.0076** |   **7.34 KB** |


## Before — unguarded

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method  | Edges | Mean          | Error         | StdDev        | Gen0    | Gen1   | Allocated |
|-------- |------ |--------------:|--------------:|--------------:|--------:|-------:|----------:|
| **Decide**  | **1**     |      **8.675 ns** |      **8.686 ns** |     **0.4761 ns** |  **0.0038** |      **-** |      **32 B** |
| Enabled | 1     |     68.030 ns |     74.113 ns |     4.0624 ns |  0.0659 |      - |     552 B |
| **Decide**  | **128**   |    **303.952 ns** |    **293.813 ns** |    **16.1049 ns** |  **0.0038** |      **-** |      **32 B** |
| Enabled | 128   |  3,986.017 ns |  4,880.058 ns |   267.4924 ns |  1.2512 | 0.0153 |   10472 B |
| **Decide**  | **1024**  |  **2,699.393 ns** |    **163.783 ns** |     **8.9775 ns** |  **0.0038** |      **-** |      **32 B** |
| Enabled | 1024  | 47,514.220 ns | 80,053.521 ns | 4,388.0036 ns | 11.2305 | 1.8311 |   94168 B |


## After — guarded

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method           | Edges | Mean     | Error     | StdDev  | Gen0   | Allocated |
|----------------- |------ |---------:|----------:|--------:|-------:|----------:|
| **GuardedAdmission** | **1**     | **227.9 ns** |  **22.87 ns** | **1.25 ns** | **0.0591** |     **496 B** |
| **GuardedAdmission** | **128**   | **223.9 ns** | **128.44 ns** | **7.04 ns** | **0.0591** |     **496 B** |
| **GuardedAdmission** | **1024**  | **229.0 ns** |  **45.69 ns** | **2.50 ns** | **0.0591** |     **496 B** |


## After — unguarded

```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method  | Edges | Mean      | Error      | StdDev    | Allocated |
|-------- |------ |----------:|-----------:|----------:|----------:|
| **Decide**  | **1**     | **29.842 ns** |  **1.8305 ns** | **0.1003 ns** |         **-** |
| Enabled | 1     |  8.134 ns |  2.6262 ns | 0.1439 ns |         - |
| **Decide**  | **128**   | **29.014 ns** |  **6.2210 ns** | **0.3410 ns** |         **-** |
| Enabled | 128   |  7.931 ns |  2.6254 ns | 0.1439 ns |         - |
| **Decide**  | **1024**  | **32.455 ns** | **19.2041 ns** | **1.0526 ns** |         **-** |
| Enabled | 1024  |  7.348 ns |  0.1862 ns | 0.0102 ns |         - |
