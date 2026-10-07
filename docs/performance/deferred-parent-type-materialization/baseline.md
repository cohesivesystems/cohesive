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
| **Prepare** | **collection** | **1,512.14 μs** | **712.862 μs** | **372.840 μs** |  **46.8750** |        **-** |        **-** |  **434.19 KB** |
| **Prepare** | **flat**       |   **146.83 μs** |   **3.083 μs** |   **1.613 μs** |        **-** |        **-** |        **-** |   **62.22 KB** |
| **Prepare** | **large**      | **2,200.71 μs** | **206.205 μs** |  **91.556 μs** | **234.3750** | **234.3750** | **234.3750** | **1720.26 KB** |
| **Prepare** | **nested**     |    **90.76 μs** |   **1.375 μs** |   **0.719 μs** |        **-** |        **-** |        **-** |  **100.89 KB** |
