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
| **Prepare** | **collection** |   **615.95 μs** | **127.737 μs** | **66.809 μs** |  **15.6250** |        **-** |        **-** | **203.22 KB** |
| **Prepare** | **duplicates** | **7,477.50 μs** |  **94.360 μs** | **49.352 μs** | **234.3750** |  **46.8750** |        **-** | **1958.6 KB** |
| **Prepare** | **flat**       |    **57.73 μs** |   **0.768 μs** |  **0.402 μs** |        **-** |        **-** |        **-** |  **33.79 KB** |
| **Prepare** | **large**      | **1,714.34 μs** |   **7.801 μs** |  **4.080 μs** | **421.8750** | **421.8750** | **109.3750** |  **821.9 KB** |
| **Prepare** | **nested**     |    **80.29 μs** |   **0.852 μs** |  **0.445 μs** |        **-** |        **-** |        **-** |  **61.41 KB** |
