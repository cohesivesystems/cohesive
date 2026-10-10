```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Scenario           | Mean         | Error       | StdDev     | Allocated |
|--------- |------------------- |-------------:|------------:|-----------:|----------:|
| **Validate** | **enum-first**         |     **8.062 ns** |   **0.3213 ns** |  **0.1427 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **121.138 ns** |   **1.0416 ns** |  **0.3714 ns** |     **210 B** |
| **Validate** | **enum-last**          |    **32.670 ns** |   **1.8210 ns** |  **0.8085 ns** |       **1 B** |
| **Validate** | **named-alias**        |    **57.446 ns** |   **6.4746 ns** |  **3.3863 ns** |       **1 B** |
| **Validate** | **named-first**        |    **24.265 ns** |   **0.0530 ns** |  **0.0189 ns** |       **1 B** |
| **Validate** | **named-invalid**      |   **121.914 ns** |   **6.0162 ns** |  **3.1466 ns** |     **169 B** |
| **Validate** | **named-last**         |    **54.823 ns** |   **3.9317 ns** |  **1.7457 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,318.199 ns** |  **46.6075 ns** | **24.3766 ns** |     **458 B** |
| **Validate** | **object-last-error**  | **8,722.211 ns** | **165.0615 ns** | **86.3304 ns** |     **466 B** |
| **Validate** | **object-valid**       | **3,937.974 ns** | **159.3097 ns** | **83.3221 ns** |       **1 B** |
| **Validate** | **union-first**        |    **45.076 ns** |   **1.5970 ns** |  **0.7091 ns** |       **1 B** |
| **Validate** | **union-invalid**      |   **196.476 ns** |   **5.2081 ns** |  **2.3124 ns** |     **314 B** |
| **Validate** | **union-last**         |    **68.450 ns** |   **5.2088 ns** |  **2.7243 ns** |       **1 B** |
