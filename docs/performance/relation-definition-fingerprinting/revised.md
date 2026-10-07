```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method                        | Shape      | Mean        | Error       | StdDev    | Ratio | RatioSD | Gen0     | Gen1    | Allocated  | Alloc Ratio |
|------------------------------ |----------- |------------:|------------:|----------:|------:|--------:|---------:|--------:|-----------:|------------:|
| **MutableTreeWithSharedMetadata** | **collection** |   **830.73 μs** |   **890.73 μs** | **48.824 μs** |  **1.00** |    **0.07** |  **62.5000** |       **-** |  **574.97 KB** |        **1.00** |
| Fingerprint                   | collection |   515.71 μs |   124.13 μs |  6.804 μs |  0.62 |    0.03 |        - |       - |  183.62 KB |        0.32 |
|                               |            |             |             |           |       |         |          |         |            |             |
| **MutableTreeWithSharedMetadata** | **flat**       |    **62.04 μs** |    **54.17 μs** |  **2.969 μs** |  **1.00** |    **0.06** |        **-** |       **-** |   **54.01 KB** |        **1.00** |
| Fingerprint                   | flat       |    49.21 μs |    32.97 μs |  1.807 μs |  0.79 |    0.04 |        - |       - |   20.37 KB |        0.38 |
|                               |            |             |             |           |       |         |          |         |            |             |
| **MutableTreeWithSharedMetadata** | **large**      | **2,861.54 μs** | **1,283.99 μs** | **70.380 μs** |  **1.00** |    **0.03** | **187.5000** | **62.5000** | **2039.53 KB** |        **1.00** |
| Fingerprint                   | large      | 2,360.97 μs |   778.29 μs | 42.661 μs |  0.83 |    0.02 |  62.5000 |       - |   682.7 KB |        0.33 |
|                               |            |             |             |           |       |         |          |         |            |             |
| **MutableTreeWithSharedMetadata** | **nested**     |   **118.60 μs** |    **87.50 μs** |  **4.796 μs** |  **1.00** |    **0.05** |        **-** |       **-** |      **98 KB** |        **1.00** |
| Fingerprint                   | nested     |   100.21 μs |    98.79 μs |  5.415 μs |  0.85 |    0.05 |        - |       - |   39.77 KB |        0.41 |
