```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-UWLSOM : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=10  LaunchCount=1  WarmupCount=3

```
| Method           | OtherFields | OwnedStorage | Mean        | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------- |------------ |------------- |------------:|---------:|---------:|------:|--------:|----------:|------------:|
| **InterfaceScan**    | **16**          | **False**        |    **13.40 ns** | **0.472 ns** | **0.312 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| Lookup           | 16          | False        |    36.33 ns | 0.966 ns | 0.639 ns |  2.71 |    0.08 |         - |          NA |
| ValidateContract | 16          | False        |    50.63 ns | 0.290 ns | 0.192 ns |  3.78 |    0.09 |         - |          NA |
|                  |             |              |             |          |          |       |         |           |             |
| **InterfaceScan**    | **16**          | **True**         |    **12.60 ns** | **0.536 ns** | **0.355 ns** |  **1.00** |    **0.04** |         **-** |          **NA** |
| Lookup           | 16          | True         |    33.22 ns | 0.326 ns | 0.216 ns |  2.64 |    0.07 |         - |          NA |
| ValidateContract | 16          | True         |    49.36 ns | 0.297 ns | 0.196 ns |  3.92 |    0.10 |         - |          NA |
|                  |             |              |             |          |          |       |         |           |             |
| **InterfaceScan**    | **1024**        | **False**        |   **716.73 ns** | **6.028 ns** | **3.587 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Lookup           | 1024        | False        | 1,753.44 ns | 1.467 ns | 0.970 ns |  2.45 |    0.01 |         - |          NA |
| ValidateContract | 1024        | False        | 1,797.77 ns | 2.515 ns | 1.497 ns |  2.51 |    0.01 |         - |          NA |
|                  |             |              |             |          |          |       |         |           |             |
| **InterfaceScan**    | **1024**        | **True**         |   **713.47 ns** | **1.296 ns** | **0.771 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Lookup           | 1024        | True         | 1,763.32 ns | 3.840 ns | 2.285 ns |  2.47 |    0.00 |         - |          NA |
| ValidateContract | 1024        | True         | 1,784.90 ns | 0.252 ns | 0.132 ns |  2.50 |    0.00 |         - |          NA |
