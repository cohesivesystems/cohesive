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
| **FreshGraphAdmission** | **collection** | **False**       |  **6.798 μs** | **0.0372 μs** | **0.0221 μs** | **1.6861** | **0.0381** | **0.0153** |  **13.76 KB** |
| **FreshGraphAdmission** | **collection** | **True**        | **10.229 μs** | **0.1901 μs** | **0.1257 μs** | **2.2583** | **0.0610** |      **-** |  **18.53 KB** |
| **FreshGraphAdmission** | **flat**       | **False**       |  **3.350 μs** | **0.0260 μs** | **0.0172 μs** | **1.0529** |      **-** |      **-** |   **8.63 KB** |
| **FreshGraphAdmission** | **flat**       | **True**        |  **6.453 μs** | **0.0323 μs** | **0.0214 μs** | **1.4648** |      **-** |      **-** |     **12 KB** |
| **FreshGraphAdmission** | **large**      | **False**       | **35.862 μs** | **0.0841 μs** | **0.0556 μs** | **1.6479** | **0.0610** |      **-** |  **13.76 KB** |
| **FreshGraphAdmission** | **large**      | **True**        | **40.779 μs** | **0.2285 μs** | **0.1360 μs** | **2.1973** | **0.1221** |      **-** |  **18.53 KB** |
| **FreshGraphAdmission** | **nested**     | **False**       |  **6.053 μs** | **0.0249 μs** | **0.0148 μs** | **1.6785** | **0.0610** | **0.0305** |  **13.81 KB** |
| **FreshGraphAdmission** | **nested**     | **True**        |  **8.442 μs** | **0.0893 μs** | **0.0467 μs** | **2.0142** | **0.0610** |      **-** |  **16.83 KB** |
