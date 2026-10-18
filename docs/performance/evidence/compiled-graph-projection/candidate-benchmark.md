```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
LaunchCount=1  WarmupCount=3

```
| Method      | Fields | Mean         | Error      | StdDev    | Gen0     | Gen1     | Gen2    | Allocated  |
|------------ |------- |-------------:|-----------:|----------:|---------:|---------:|--------:|-----------:|
| **Project**     | **1**      |     **4.058 μs** |  **0.0425 μs** | **0.0110 μs** |   **0.7437** |   **0.0162** |       **-** |    **6.13 KB** |
| DecodeFresh | 1      |     5.787 μs |  0.0797 μs | 0.0123 μs |   0.7859 |   0.3236 |  0.0925 |    5.72 KB |
| **Project**     | **128**    |   **247.212 μs** | **13.7205 μs** | **3.5632 μs** |  **23.0769** |   **8.6538** |       **-** |  **191.88 KB** |
| DecodeFresh | 128    |   245.579 μs |  1.8791 μs | 0.2908 μs |  29.3561 |  12.3106 |  3.7879 |  212.17 KB |
| **Project**     | **1024**   | **2,038.178 μs** | **11.4181 μs** | **1.7670 μs** | **179.6875** |  **85.9375** |       **-** |  **1504.4 KB** |
| DecodeFresh | 1024   | 2,004.891 μs | 19.1699 μs | 4.9784 μs | 234.3750 | 101.5625 | 31.2500 | 1668.53 KB |
