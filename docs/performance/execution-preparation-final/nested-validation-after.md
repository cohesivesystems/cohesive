```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Input   | Mean        | Error     | StdDev    | Gen0   | Allocated |
|--------- |----------- |-------- |------------:|----------:|----------:|-------:|----------:|
| **Validate** | **collection** | **case**    | **29,229.9 ns** | **126.64 ns** |  **56.23 ns** | **4.3945** |   **38145 B** |
| **Validate** | **collection** | **exact**   |  **9,562.4 ns** | **756.06 ns** | **335.70 ns** |      **-** |         **-** |
| **Validate** | **collection** | **unknown** |  **1,560.6 ns** |  **15.95 ns** |   **8.34 ns** |      **-** |     **745 B** |
| **Validate** | **flat**       | **case**    |    **901.0 ns** |  **11.19 ns** |   **5.85 ns** |      **-** |    **1193 B** |
| **Validate** | **flat**       | **exact**   |    **291.4 ns** |   **2.28 ns** |   **1.19 ns** |      **-** |       **1 B** |
| **Validate** | **flat**       | **unknown** |  **1,423.0 ns** |  **21.39 ns** |  **11.19 ns** |      **-** |     **361 B** |
| **Validate** | **large**      | **case**    |  **7,777.8 ns** | **284.41 ns** | **148.75 ns** | **0.7324** |    **7121 B** |
| **Validate** | **large**      | **exact**   |  **2,810.1 ns** |  **25.11 ns** |  **13.13 ns** |      **-** |       **1 B** |
| **Validate** | **large**      | **unknown** |  **7,525.3 ns** |  **37.21 ns** |  **19.46 ns** |      **-** |     **361 B** |
| **Validate** | **nested**     | **case**    |  **1,049.5 ns** |  **74.82 ns** |  **39.13 ns** |      **-** |    **1193 B** |
| **Validate** | **nested**     | **exact**   |    **391.0 ns** |   **6.93 ns** |   **3.62 ns** |      **-** |       **1 B** |
| **Validate** | **nested**     | **unknown** |  **1,421.1 ns** |   **9.59 ns** |   **5.01 ns** |      **-** |    **1177 B** |
