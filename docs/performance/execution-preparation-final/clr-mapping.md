```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean         | Error      | StdDev     | Allocated |
|--------- |----------- |-------------:|-----------:|-----------:|----------:|
| **Map**      | **collection** |     **24.49 ns** |   **6.881 ns** |   **3.599 ns** |         **-** |
| Traverse | collection |  8,574.95 ns | 490.519 ns | 256.551 ns |    3403 B |
| **Map**      | **flat**       |     **15.91 ns** |   **5.803 ns** |   **2.577 ns** |         **-** |
| Traverse | flat       |  1,585.61 ns |  29.333 ns |  10.460 ns |    1327 B |
| **Map**      | **large**      |     **13.67 ns** |   **2.488 ns** |   **1.301 ns** |         **-** |
| Traverse | large      | 11,735.58 ns | 142.575 ns |  63.304 ns |    4154 B |
| **Map**      | **nested**     |     **24.09 ns** |  **14.716 ns** |   **6.534 ns** |         **-** |
| Traverse | nested     |  4,863.86 ns | 233.223 ns | 121.980 ns |    2332 B |
