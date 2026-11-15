```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-UWLSOM : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=10  LaunchCount=1  WarmupCount=3

```
| Method              | Workload   | Constrained | Mean      | Error     | StdDev    | Gen0   | Gen1   | Gen2   | Allocated |
|-------------------- |----------- |------------ |----------:|----------:|----------:|-------:|-------:|-------:|----------:|
| **FreshGraphAdmission** | **collection** | **False**       |  **6.601 μs** | **0.0120 μs** | **0.0063 μs** | **1.6479** | **0.0610** |      **-** |  **13.85 KB** |
| **FreshGraphAdmission** | **collection** | **True**        | **10.098 μs** | **0.0496 μs** | **0.0259 μs** | **2.2583** | **0.0610** |      **-** |   **18.5 KB** |
| **FreshGraphAdmission** | **flat**       | **False**       |  **3.408 μs** | **0.0095 μs** | **0.0063 μs** | **1.0529** |      **-** |      **-** |   **8.71 KB** |
| **FreshGraphAdmission** | **flat**       | **True**        |  **6.197 μs** | **0.0341 μs** | **0.0226 μs** | **1.4648** |      **-** |      **-** |  **12.03 KB** |
| **FreshGraphAdmission** | **large**      | **False**       | **35.023 μs** | **0.1070 μs** | **0.0708 μs** | **1.6479** | **0.0610** |      **-** |  **13.85 KB** |
| **FreshGraphAdmission** | **large**      | **True**        | **41.256 μs** | **0.0647 μs** | **0.0338 μs** | **2.1973** | **0.1221** |      **-** |   **18.5 KB** |
| **FreshGraphAdmission** | **nested**     | **False**       |  **6.696 μs** | **0.2360 μs** | **0.1404 μs** | **1.6785** | **0.0610** | **0.0305** |   **13.9 KB** |
| **FreshGraphAdmission** | **nested**     | **True**        |  **8.290 μs** | **0.1321 μs** | **0.0691 μs** | **2.0752** | **0.0610** |      **-** |     **17 KB** |
