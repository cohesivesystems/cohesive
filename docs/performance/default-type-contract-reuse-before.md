```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean      | Error     | StdDev    | Allocated |
|------- |----------- |----------:|----------:|----------:|----------:|
| **Map**    | **collection** | **18.990 μs** | **1.0344 μs** | **0.4593 μs** |   **8.37 KB** |
| **Map**    | **flat**       |  **1.855 μs** | **0.2290 μs** | **0.1198 μs** |   **1.05 KB** |
| **Map**    | **large**      | **65.627 μs** | **2.3656 μs** | **1.2372 μs** |  **29.96 KB** |
| **Map**    | **nested**     | **10.427 μs** | **0.3857 μs** | **0.2018 μs** |   **3.89 KB** |
