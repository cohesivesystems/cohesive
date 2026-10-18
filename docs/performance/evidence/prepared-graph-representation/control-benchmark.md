```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms  
LaunchCount=1  WarmupCount=3  

```
| Method                   | Fields | Mean         | Error      | StdDev     | Gen0     | Gen1     | Gen2    | Allocated  |
|------------------------- |------- |-------------:|-----------:|-----------:|---------:|---------:|--------:|-----------:|
| **Project**                  | **1**      |     **4.304 μs** |  **0.3105 μs** |  **0.0806 μs** |   **0.7389** |   **0.0172** |       **-** |    **6.13 KB** |
| AuthorAndProjectNewGraph | 1      |     4.407 μs |  0.1255 μs |  0.0194 μs |   0.7918 |   0.0176 |       - |    6.58 KB |
| DecodeNewGraphStorage    | 1      |     6.554 μs |  0.1668 μs |  0.0258 μs |   1.0106 |   0.3887 |  0.1296 |    7.34 KB |
| DecodeFresh              | 1      |     6.123 μs |  0.2314 μs |  0.0601 μs |   0.7877 |   0.3200 |  0.0985 |    5.73 KB |
| **Project**                  | **128**    |   **261.393 μs** | **12.8546 μs** |  **3.3383 μs** |  **22.9167** |   **7.2917** |       **-** |  **189.89 KB** |
| AuthorAndProjectNewGraph | 128    |   262.186 μs |  0.1941 μs |  0.0300 μs |  23.9583 |  11.4583 |       - |  199.27 KB |
| DecodeNewGraphStorage    | 128    |   254.927 μs |  8.3678 μs |  2.1731 μs |  29.7619 |  12.8968 |  3.9683 |  213.74 KB |
| DecodeFresh              | 128    |   255.358 μs |  7.0576 μs |  1.0922 μs |  29.7619 |  12.8968 |  3.9683 |  212.18 KB |
| **Project**                  | **1024**   | **2,196.786 μs** | **27.5285 μs** |  **7.1491 μs** | **179.6875** |  **85.9375** |       **-** | **1488.09 KB** |
| AuthorAndProjectNewGraph | 1024   | 2,200.966 μs | 81.6345 μs | 21.2002 μs | 187.5000 |  93.7500 |       - | 1560.47 KB |
| DecodeNewGraphStorage    | 1024   | 2,101.699 μs | 44.7862 μs |  6.9307 μs | 234.3750 | 101.5625 | 31.2500 | 1670.15 KB |
| DecodeFresh              | 1024   | 2,076.257 μs | 99.0887 μs | 15.3341 μs | 234.3750 | 101.5625 | 31.2500 | 1668.56 KB |
