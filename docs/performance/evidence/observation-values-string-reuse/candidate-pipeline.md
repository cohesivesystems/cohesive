```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean           | Error        | StdDev       | Op/s        | Gen0     | Gen1    | Gen2    | Allocated  |
|--------------------- |----------- |---------------:|-------------:|-------------:|------------:|---------:|--------:|--------:|-----------:|
| **ParseDirect**          | **collection** |    **28,367.9 ns** |    **248.80 ns** |     **64.61 ns** |    **35,251.1** |   **4.1667** |  **0.5631** |       **-** |   **34.17 KB** |
| ParseThroughDocument | collection |    33,784.7 ns |    603.21 ns |     93.35 ns |    29,599.2 |   3.7961 |  0.5423 |       - |    31.5 KB |
| ParseMapWrite        | collection |    32,132.7 ns |    367.73 ns |     56.91 ns |    31,120.9 |   4.0900 |  0.1278 |       - |   34.38 KB |
| **ParseDirect**          | **flat**       |       **651.7 ns** |     **54.49 ns** |     **14.15 ns** | **1,534,458.3** |   **0.2000** |       **-** |       **-** |    **1.64 KB** |
| ParseThroughDocument | flat       |     1,482.6 ns |     46.42 ns |      7.18 ns |   674,471.5 |   0.2058 |       - |       - |    1.71 KB |
| ParseMapWrite        | flat       |       704.0 ns |     17.16 ns |      4.46 ns | 1,420,401.6 |   0.2306 |       - |       - |    1.89 KB |
| **ParseDirect**          | **large**      | **1,368,254.4 ns** | **24,566.16 ns** |  **6,379.75 ns** |       **730.9** | **125.0000** | **62.5000** | **41.6667** |  **1003.3 KB** |
| ParseThroughDocument | large      | 1,481,003.8 ns | 10,808.55 ns |  2,806.95 ns |       675.2 | 125.0000 | 62.5000 | 39.7727 |   995.9 KB |
| ParseMapWrite        | large      | 1,465,327.0 ns | 95,688.10 ns | 24,849.90 ns |       682.4 | 125.0000 | 62.5000 | 39.7727 | 1003.19 KB |
| **ParseDirect**          | **nested**     |     **1,759.4 ns** |     **81.95 ns** |     **12.68 ns** |   **568,361.2** |   **0.5632** |  **0.0137** |       **-** |    **4.61 KB** |
| ParseThroughDocument | nested     |     3,455.9 ns |     10.56 ns |      2.74 ns |   289,361.1 |   0.5767 |  0.0137 |       - |    4.71 KB |
| ParseMapWrite        | nested     |     1,979.6 ns |     85.06 ns |     13.16 ns |   505,164.2 |   0.5878 |       - |       - |    4.82 KB |
