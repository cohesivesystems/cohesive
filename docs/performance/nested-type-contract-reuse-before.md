```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean      | Error     | StdDev    | Allocated |
|--------- |----------- |----------:|----------:|----------:|----------:|
| **Traverse** | **collection** | **18.144 μs** | **0.5500 μs** | **0.2442 μs** |    **8569 B** |
| **Traverse** | **flat**       |  **1.980 μs** | **0.3060 μs** | **0.1359 μs** |    **1001 B** |
| **Traverse** | **large**      | **62.458 μs** | **3.1064 μs** | **1.6247 μs** |   **30674 B** |
| **Traverse** | **nested**     |  **7.787 μs** | **0.5664 μs** | **0.2962 μs** |    **3983 B** |
