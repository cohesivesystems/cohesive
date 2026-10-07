```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method | Shape      | Mean       | Error     | StdDev    | Allocated |
|------- |----------- |-----------:|----------:|----------:|----------:|
| **Map**    | **collection** |  **47.890 μs** | **15.354 μs** | **0.8416 μs** |  **36.63 KB** |
| **Map**    | **flat**       |   **7.477 μs** |  **4.034 μs** | **0.2211 μs** |   **7.68 KB** |
| **Map**    | **large**      | **152.990 μs** | **48.582 μs** | **2.6630 μs** | **101.35 KB** |
| **Map**    | **nested**     |  **24.128 μs** |  **3.754 μs** | **0.2057 μs** |  **19.53 KB** |
