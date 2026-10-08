```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean      | Error      | StdDev     | Allocated |
|------- |----------- |----------:|-----------:|-----------:|----------:|
| **Map**    | **collection** | **20.001 μs** |  **1.1456 μs** |  **0.5992 μs** |   **8.37 KB** |
| **Map**    | **flat**       |  **1.889 μs** |  **0.2323 μs** |  **0.1215 μs** |   **1.05 KB** |
| **Map**    | **large**      | **79.421 μs** | **36.4505 μs** | **19.0643 μs** |  **29.96 KB** |
| **Map**    | **nested**     | **10.448 μs** |  **0.3204 μs** |  **0.1676 μs** |   **3.88 KB** |
