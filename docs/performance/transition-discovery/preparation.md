```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method  | Operations | CompleteInput | Mean        | Error       | StdDev      | Gen0    | Gen1   | Allocated |
|-------- |----------- |-------------- |------------:|------------:|------------:|--------:|-------:|----------:|
| **Inspect** | **1**          | **False**         |    **518.0 ns** |    **238.4 ns** |    **13.07 ns** |  **0.4177** | **0.0010** |   **3.41 KB** |
| **Inspect** | **1**          | **True**          |  **2,028.8 ns** |    **845.7 ns** |    **46.36 ns** |  **1.3390** | **0.0114** |  **10.96 KB** |
| **Inspect** | **8**          | **False**         |  **2,494.7 ns** |    **124.6 ns** |     **6.83 ns** |  **2.0523** | **0.0114** |  **16.78 KB** |
| **Inspect** | **8**          | **True**          | **14,098.8 ns** |  **1,782.9 ns** |    **97.72 ns** |  **9.4299** | **0.1221** |  **77.11 KB** |
| **Inspect** | **32**         | **False**         |  **9,739.1 ns** |  **1,143.0 ns** |    **62.65 ns** |  **7.7057** | **0.1068** |  **63.05 KB** |
| **Inspect** | **32**         | **True**          | **57,788.0 ns** | **22,035.5 ns** | **1,207.84 ns** | **37.2314** | **0.7935** | **304.55 KB** |
