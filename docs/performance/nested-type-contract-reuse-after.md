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
| **Traverse** | **collection** |  **9.228 μs** | **0.3293 μs** | **0.1722 μs** |   **3.25 KB** |
| **Traverse** | **flat**       |  **1.978 μs** | **0.5399 μs** | **0.2824 μs** |   **1.28 KB** |
| **Traverse** | **large**      | **13.128 μs** | **0.2238 μs** | **0.1171 μs** |   **4.03 KB** |
| **Traverse** | **nested**     |  **5.616 μs** | **0.3206 μs** | **0.1677 μs** |   **2.25 KB** |
