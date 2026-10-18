```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
LaunchCount=1  WarmupCount=3

```
| Method      | Fields | Mean         | Error       | StdDev     | Gen0     | Gen1     | Gen2    | Allocated  |
|------------ |------- |-------------:|------------:|-----------:|---------:|---------:|--------:|-----------:|
| **Project**     | **1**      |     **4.049 μs** |   **0.0865 μs** |  **0.0134 μs** |   **0.7423** |   **0.0161** |       **-** |    **6.13 KB** |
| DecodeFresh | 1      |     8.147 μs |   0.0649 μs |  0.0100 μs |   1.1022 |   0.3890 |  0.1297 |    8.14 KB |
| **Project**     | **128**    |   **229.395 μs** |   **5.0894 μs** |  **1.3217 μs** |  **29.8913** |  **11.7754** |       **-** |  **245.03 KB** |
| DecodeFresh | 128    |   367.064 μs |   4.7281 μs |  0.7317 μs |  36.9318 |  15.6250 |  4.2614 |  272.35 KB |
| **Project**     | **1024**   | **1,971.696 μs** |   **7.6111 μs** |  **1.1778 μs** | **234.3750** | **117.1875** |       **-** |  **1932.1 KB** |
| DecodeFresh | 1024   | 2,962.300 μs | 118.7503 μs | 30.8391 μs | 291.6667 | 114.5833 | 31.2500 | 2135.77 KB |
