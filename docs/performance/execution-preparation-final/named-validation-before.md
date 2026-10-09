```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean       | Error     | StdDev    | Allocated |
|--------- |----------- |-----------:|----------:|----------:|----------:|
| **Validate** | **collection** | **9,005.1 ns** | **211.30 ns** | **110.51 ns** |         **-** |
| **Validate** | **flat**       |   **281.2 ns** |   **3.25 ns** |   **1.70 ns** |       **1 B** |
| **Validate** | **large**      | **2,694.4 ns** |  **84.96 ns** |  **44.44 ns** |       **1 B** |
| **Validate** | **nested**     |   **519.6 ns** |  **10.36 ns** |   **4.60 ns** |       **1 B** |
