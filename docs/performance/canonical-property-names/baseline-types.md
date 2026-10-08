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
| **Prepare** | **collection** |   **666.01 μs** | **176.296 μs** | **78.276 μs** |  **31.2500** |        **-** |        **-** |  **310.24 KB** |
| **Prepare** | **flat**       |    **88.04 μs** |   **2.485 μs** |  **1.300 μs** |        **-** |        **-** |        **-** |    **46.2 KB** |
| **Prepare** | **large**      | **2,032.39 μs** |  **63.678 μs** | **33.305 μs** | **531.2500** | **531.2500** | **140.6250** | **1257.05 KB** |
| **Prepare** | **nested**     |    **91.26 μs** |   **3.705 μs** |  **1.938 μs** |        **-** |        **-** |        **-** |   **75.27 KB** |
