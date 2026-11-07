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
| **Inspect** | **1**          | **False**         |    **518.8 ns** |    **159.39 ns** |   **8.74 ns** |  **0.4177** | **0.0010** |   **3.41 KB** |
| **Inspect** | **1**          | **True**          |  **1,558.1 ns** |     **62.39 ns** |   **3.42 ns** |  **1.1806** | **0.0134** |   **9.66 KB** |
| **Inspect** | **8**          | **False**         |  **2,501.6 ns** |    **136.35 ns** |   **7.47 ns** |  **2.0523** | **0.0114** |  **16.78 KB** |
| **Inspect** | **8**          | **True**          | **11,242.4 ns** |    **746.52 ns** |  **40.92 ns** |  **8.1635** | **0.1068** |  **66.74 KB** |
| **Inspect** | **32**         | **False**         | **10,189.2 ns** | **15,128.39 ns** | **829.24 ns** |  **7.7057** | **0.1068** |  **63.05 KB** |
| **Inspect** | **32**         | **True**          | **43,127.8 ns** |  **1,560.59 ns** |  **85.54 ns** | **32.1655** | **0.7324** | **263.04 KB** |
