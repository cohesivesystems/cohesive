```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method  | Shape      | Mean        | Error      | StdDev    | Gen0     | Gen1     | Gen2     | Allocated  |
|-------- |----------- |------------:|-----------:|----------:|---------:|---------:|---------:|-----------:|
| **Prepare** | **collection** | **1,432.08 μs** | **123.152 μs** | **64.411 μs** |  **15.6250** |        **-** |        **-** |  **203.13 KB** |
| **Prepare** | **duplicates** | **7,285.24 μs** |  **31.863 μs** | **16.665 μs** | **234.3750** |  **46.8750** |        **-** | **1958.69 KB** |
| **Prepare** | **flat**       |    **57.51 μs** |   **0.632 μs** |  **0.331 μs** |        **-** |        **-** |        **-** |   **33.46 KB** |
| **Prepare** | **large**      | **1,719.94 μs** |  **27.553 μs** | **14.411 μs** | **656.2500** | **656.2500** | **109.3750** |  **818.84 KB** |
| **Prepare** | **nested**     |    **78.25 μs** |   **1.007 μs** |  **0.447 μs** |        **-** |        **-** |        **-** |    **61.4 KB** |
