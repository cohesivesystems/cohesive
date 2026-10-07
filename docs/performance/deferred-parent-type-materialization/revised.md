```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method  | Shape      | Mean        | Error      | StdDev     | Gen0     | Gen1     | Gen2     | Allocated  |
|-------- |----------- |------------:|-----------:|-----------:|---------:|---------:|---------:|-----------:|
| **Prepare** | **collection** | **1,558.51 μs** | **337.755 μs** | **176.652 μs** |  **46.8750** |        **-** |        **-** |  **396.69 KB** |
| **Prepare** | **flat**       |   **173.60 μs** |   **2.927 μs** |   **1.300 μs** |        **-** |        **-** |        **-** |   **57.33 KB** |
| **Prepare** | **large**      | **1,957.52 μs** | **105.851 μs** |  **46.999 μs** | **203.1250** | **203.1250** | **203.1250** | **1570.39 KB** |
| **Prepare** | **nested**     |    **91.37 μs** |   **3.448 μs** |   **1.803 μs** |        **-** |        **-** |        **-** |   **93.28 KB** |
