```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean       | Error    | StdDev   | Allocated |
|--------- |----------- |-----------:|---------:|---------:|----------:|
| **Validate** | **collection** | **8,162.8 ns** | **82.69 ns** | **36.72 ns** |         **-** |
| **Validate** | **flat**       |   **261.6 ns** | **13.25 ns** |  **6.93 ns** |       **1 B** |
| **Validate** | **large**      | **2,375.8 ns** | **30.76 ns** | **13.66 ns** |       **1 B** |
| **Validate** | **nested**     |   **418.5 ns** | **25.29 ns** | **13.23 ns** |       **1 B** |
