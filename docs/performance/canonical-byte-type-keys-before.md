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
| **Prepare** | **collection** | **1,558.04 μs** | **497.901 μs** | **260.412 μs** |  **15.6250** |        **-** |        **-** |  **217.84 KB** |
| **Prepare** | **duplicates** | **7,517.88 μs** |  **41.306 μs** |  **21.604 μs** | **453.1250** |  **93.7500** |        **-** | **3839.89 KB** |
| **Prepare** | **flat**       |    **57.35 μs** |   **0.839 μs** |   **0.439 μs** |        **-** |        **-** |        **-** |   **35.32 KB** |
| **Prepare** | **large**      | **1,787.09 μs** |  **13.919 μs** |   **7.280 μs** | **609.3750** | **609.3750** | **140.6250** |  **885.87 KB** |
| **Prepare** | **nested**     |    **78.03 μs** |   **0.789 μs** |   **0.412 μs** |        **-** |        **-** |        **-** |   **63.46 KB** |
