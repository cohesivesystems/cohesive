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
| **Validate** | **enum-first**         |     **7.133 ns** |   **0.0270 ns** |  **0.0141 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **534.545 ns** |   **4.1193 ns** |  **1.8290 ns** |     **209 B** |
| **Validate** | **enum-last**          |   **265.288 ns** |   **4.1452 ns** |  **2.1680 ns** |       **1 B** |
| **Validate** | **named-alias**        | **1,485.783 ns** |   **8.2339 ns** |  **3.6559 ns** |       **1 B** |
| **Validate** | **named-first**        |    **30.330 ns** |   **0.7336 ns** |  **0.3257 ns** |       **1 B** |
| **Validate** | **named-invalid**      | **3,057.325 ns** | **113.3154 ns** | **59.2662 ns** |     **169 B** |
| **Validate** | **named-last**         | **1,543.499 ns** | **107.8166 ns** | **47.8712 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,310.114 ns** |  **94.0066 ns** | **41.7395 ns** |     **458 B** |
| **Validate** | **object-last-error**  | **8,523.407 ns** | **112.4953 ns** | **58.8372 ns** |     **466 B** |
| **Validate** | **object-valid**       | **3,985.350 ns** |  **83.0069 ns** | **43.4142 ns** |       **1 B** |
| **Validate** | **union-first**        |    **46.103 ns** |   **1.0092 ns** |  **0.5278 ns** |       **1 B** |
| **Validate** | **union-invalid**      | **2,134.885 ns** |  **16.3807 ns** |  **7.2732 ns** |     **314 B** |
| **Validate** | **union-last**         | **1,026.646 ns** |   **2.1531 ns** |  **0.9560 ns** |       **1 B** |
