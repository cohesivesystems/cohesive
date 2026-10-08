```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean       | Error       | StdDev     | Gen0    | Allocated |
|------- |----------- |-----------:|------------:|-----------:|--------:|----------:|
| **Map**    | **collection** | **161.177 μs** |   **4.5303 μs** |  **2.3694 μs** |       **-** | **107.79 KB** |
| **Map**    | **flat**       |   **3.963 μs** |   **0.3188 μs** |  **0.1668 μs** |       **-** |   **2.88 KB** |
| **Map**    | **large**      | **780.186 μs** | **106.4878 μs** | **47.2813 μs** | **46.8750** | **424.14 KB** |
| **Map**    | **nested**     | **120.446 μs** |   **2.1032 μs** |  **1.1000 μs** |       **-** |  **52.94 KB** |
