```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Scenario           | Mean         | Error       | StdDev     | Allocated |
|--------- |------------------- |-------------:|------------:|-----------:|----------:|
| **Validate** | **enum-first**         |     **7.423 ns** |   **0.0434 ns** |  **0.0192 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **117.117 ns** |   **0.1909 ns** |  **0.0999 ns** |     **209 B** |
| **Validate** | **enum-last**          |    **28.931 ns** |   **0.0293 ns** |  **0.0153 ns** |       **1 B** |
| **Validate** | **named-alias**        |   **120.847 ns** |   **1.0468 ns** |  **0.4648 ns** |       **1 B** |
| **Validate** | **named-first**        |    **29.481 ns** |   **2.2447 ns** |  **1.1740 ns** |       **1 B** |
| **Validate** | **named-invalid**      |   **253.337 ns** |   **9.8694 ns** |  **4.3821 ns** |     **169 B** |
| **Validate** | **named-last**         |   **123.878 ns** |   **5.5152 ns** |  **2.4488 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,358.096 ns** |  **88.1712 ns** | **39.1485 ns** |     **457 B** |
| **Validate** | **object-last-error**  | **8,446.859 ns** |  **84.6396 ns** | **37.5805 ns** |     **465 B** |
| **Validate** | **object-valid**       | **3,920.802 ns** | **141.2169 ns** | **73.8592 ns** |       **1 B** |
| **Validate** | **union-first**        |    **43.751 ns** |   **1.1291 ns** |  **0.5013 ns** |       **1 B** |
| **Validate** | **union-invalid**      |   **294.304 ns** |   **5.8198 ns** |  **2.5840 ns** |     **313 B** |
| **Validate** | **union-last**         |   **115.492 ns** |   **3.5464 ns** |  **1.8548 ns** |       **1 B** |
