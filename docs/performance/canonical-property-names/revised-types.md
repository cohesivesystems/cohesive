```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method  | Shape      | Mean        | Error      | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|-------- |----------- |------------:|-----------:|----------:|---------:|---------:|---------:|----------:|
| **Prepare** | **collection** |   **656.62 μs** | **167.155 μs** | **87.425 μs** |  **15.6250** |        **-** |        **-** | **217.94 KB** |
| **Prepare** | **flat**       |    **91.51 μs** |   **7.292 μs** |  **3.814 μs** |        **-** |        **-** |        **-** |  **35.58 KB** |
| **Prepare** | **large**      | **1,949.77 μs** | **140.887 μs** | **73.686 μs** | **531.2500** | **531.2500** | **140.6250** | **886.06 KB** |
| **Prepare** | **nested**     |    **83.03 μs** |   **2.873 μs** |  **1.503 μs** |        **-** |        **-** |        **-** |  **63.49 KB** |
