```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method  | Shape      | Mean       | Error       | StdDev    | Gen0     | Gen1     | Gen2     | Allocated  |
|-------- |----------- |-----------:|------------:|----------:|---------:|---------:|---------:|-----------:|
| **Prepare** | **collection** | **1,908.9 μs** | **1,023.52 μs** |  **56.10 μs** |  **62.5000** |        **-** |        **-** |  **608.78 KB** |
| **Prepare** | **flat**       |   **258.6 μs** |   **178.28 μs** |   **9.77 μs** |        **-** |        **-** |        **-** |   **88.63 KB** |
| **Prepare** | **large**      | **6,313.6 μs** | **5,251.11 μs** | **287.83 μs** | **187.5000** | **187.5000** | **187.5000** | **2461.37 KB** |
| **Prepare** | **nested**     |   **306.6 μs** |    **91.08 μs** |   **4.99 μs** |        **-** |        **-** |        **-** |  **139.16 KB** |
