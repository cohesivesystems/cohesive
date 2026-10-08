```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean       | Error     | StdDev    | Allocated |
|------- |----------- |-----------:|----------:|----------:|----------:|
| **Map**    | **collection** |  **40.403 μs** | **1.6171 μs** | **0.7180 μs** |   **23.4 KB** |
| **Map**    | **flat**       |   **3.873 μs** | **0.4184 μs** | **0.2188 μs** |   **2.43 KB** |
| **Map**    | **large**      | **142.142 μs** | **7.5151 μs** | **3.9305 μs** |  **86.47 KB** |
| **Map**    | **nested**     |  **17.774 μs** | **0.8536 μs** | **0.3790 μs** |  **10.73 KB** |
