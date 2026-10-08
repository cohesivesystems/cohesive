```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method  | Shape      | Mean        | Error      | StdDev     | Gen0     | Gen1     | Gen2     | Allocated  |
|-------- |----------- |------------:|-----------:|-----------:|---------:|---------:|---------:|-----------:|
| **Prepare** | **collection** | **1,680.99 μs** | **612.006 μs** | **320.091 μs** |  **31.2500** |        **-** |        **-** |  **376.24 KB** |
| **Prepare** | **flat**       |   **177.30 μs** |  **24.563 μs** |  **12.847 μs** |        **-** |        **-** |        **-** |   **54.83 KB** |
| **Prepare** | **large**      | **2,346.57 μs** | **346.216 μs** | **181.078 μs** | **750.0000** | **750.0000** | **203.1250** | **1539.04 KB** |
| **Prepare** | **nested**     |    **94.27 μs** |   **6.389 μs** |   **3.341 μs** |        **-** |        **-** |        **-** |   **84.07 KB** |
