```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Scenario           | Mean         | Error      | StdDev     | Allocated |
|--------- |------------------- |-------------:|-----------:|-----------:|----------:|
| **Validate** | **enum-first**         |     **8.803 ns** |  **0.4909 ns** |  **0.2568 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **127.760 ns** |  **4.3082 ns** |  **2.2533 ns** |     **210 B** |
| **Validate** | **enum-last**          |    **31.638 ns** |  **0.0335 ns** |  **0.0119 ns** |       **1 B** |
| **Validate** | **named-alias**        |    **51.617 ns** |  **2.5359 ns** |  **1.1259 ns** |       **1 B** |
| **Validate** | **named-first**        |    **22.169 ns** |  **0.6504 ns** |  **0.3402 ns** |       **1 B** |
| **Validate** | **named-invalid**      |   **125.364 ns** | **12.1402 ns** |  **6.3496 ns** |     **169 B** |
| **Validate** | **named-last**         |    **53.129 ns** |  **0.7680 ns** |  **0.4017 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,502.075 ns** | **91.1235 ns** | **47.6594 ns** |     **458 B** |
| **Validate** | **object-last-error**  | **8,829.385 ns** | **56.3821 ns** | **29.4889 ns** |     **465 B** |
| **Validate** | **object-valid**       | **4,064.008 ns** | **96.3213 ns** | **50.3779 ns** |       **1 B** |
| **Validate** | **union-first**        |    **44.296 ns** |  **0.8493 ns** |  **0.4442 ns** |       **1 B** |
| **Validate** | **union-invalid**      |   **199.600 ns** |  **2.5318 ns** |  **1.1241 ns** |     **314 B** |
| **Validate** | **union-last**         |    **70.779 ns** |  **1.3754 ns** |  **0.6107 ns** |       **1 B** |
