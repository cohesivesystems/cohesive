```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method | Shape      | Mean       | Error      | StdDev    | Allocated |
|------- |----------- |-----------:|-----------:|----------:|----------:|
| **Map**    | **collection** |  **36.302 μs** |  **0.6995 μs** | **0.0383 μs** |  **23.52 KB** |
| **Map**    | **flat**       |   **3.683 μs** |  **1.4548 μs** | **0.0797 μs** |   **2.72 KB** |
| **Map**    | **large**      | **133.772 μs** | **51.5686 μs** | **2.8267 μs** |  **86.74 KB** |
| **Map**    | **nested**     |  **16.229 μs** |  **2.0712 μs** | **0.1135 μs** |  **11.03 KB** |
