```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method  | Shape      | Mean       | Error        | StdDev    | Gen0     | Gen1     | Gen2     | Allocated  |
|-------- |----------- |-----------:|-------------:|----------:|---------:|---------:|---------:|-----------:|
| **Prepare** | **collection** | **2,125.7 μs** |    **252.29 μs** |  **13.83 μs** |  **62.5000** |        **-** |        **-** |  **800.28 KB** |
| **Prepare** | **flat**       |   **283.2 μs** |    **129.62 μs** |   **7.11 μs** |        **-** |        **-** |        **-** |  **111.95 KB** |
| **Prepare** | **large**      | **9,250.5 μs** | **10,699.59 μs** | **586.48 μs** | **437.5000** | **187.5000** | **187.5000** | **3229.36 KB** |
| **Prepare** | **nested**     |   **330.1 μs** |     **48.29 μs** |   **2.65 μs** |        **-** |        **-** |        **-** |  **162.42 KB** |
