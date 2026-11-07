```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method  | Operations | CompleteInput | Mean        | Error        | StdDev    | Gen0    | Gen1   | Allocated |
|-------- |----------- |-------------- |------------:|-------------:|----------:|--------:|-------:|----------:|
| **Inspect** | **1**          | **False**         |    **646.1 ns** |     **23.51 ns** |   **1.29 ns** |  **0.5655** | **0.0010** |   **4.63 KB** |
| **Inspect** | **1**          | **True**          |  **1,976.4 ns** |    **939.94 ns** |  **51.52 ns** |  **1.3142** | **0.0095** |  **10.75 KB** |
| **Inspect** | **8**          | **False**         |  **5,741.8 ns** |  **4,230.15 ns** | **231.87 ns** |  **4.0894** | **0.0153** |  **33.41 KB** |
| **Inspect** | **8**          | **True**          | **16,419.7 ns** |  **7,919.57 ns** | **434.10 ns** | **10.0708** | **0.0610** |  **82.37 KB** |
| **Inspect** | **32**         | **False**         | **23,490.2 ns** | **14,399.31 ns** | **789.27 ns** | **16.2048** | **0.1831** | **132.55 KB** |
| **Inspect** | **32**         | **True**          | **55,129.6 ns** | **17,800.17 ns** | **975.69 ns** | **40.2222** | **0.7935** | **328.57 KB** |
