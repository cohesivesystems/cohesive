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
| **Validate** | **collection** | **case**    | **29,137.0 ns** | **155.61 ns** |  **81.39 ns** | **4.3945** |   **38145 B** |
| **Validate** | **collection** | **exact**   |  **9,660.5 ns** | **227.18 ns** | **100.87 ns** |      **-** |       **1 B** |
| **Validate** | **collection** | **unknown** |  **1,572.4 ns** |  **40.45 ns** |  **17.96 ns** |      **-** |     **745 B** |
| **Validate** | **flat**       | **case**    |    **909.1 ns** |  **34.25 ns** |  **17.91 ns** |      **-** |    **1193 B** |
| **Validate** | **flat**       | **exact**   |    **302.2 ns** |   **8.50 ns** |   **4.45 ns** |      **-** |       **1 B** |
| **Validate** | **flat**       | **unknown** |  **1,484.4 ns** |  **52.03 ns** |  **27.21 ns** |      **-** |     **361 B** |
| **Validate** | **large**      | **case**    |  **8,057.6 ns** | **373.93 ns** | **195.57 ns** | **0.7324** |    **7121 B** |
| **Validate** | **large**      | **exact**   |  **2,895.7 ns** |  **66.66 ns** |  **34.87 ns** |      **-** |       **1 B** |
| **Validate** | **large**      | **unknown** |  **7,581.2 ns** | **105.38 ns** |  **46.79 ns** |      **-** |     **361 B** |
| **Validate** | **nested**     | **case**    |  **1,040.7 ns** |   **6.05 ns** |   **3.16 ns** |      **-** |    **1193 B** |
| **Validate** | **nested**     | **exact**   |    **396.1 ns** |  **12.40 ns** |   **6.49 ns** |      **-** |       **1 B** |
| **Validate** | **nested**     | **unknown** |  **1,512.2 ns** |   **4.50 ns** |   **2.36 ns** |      **-** |    **1177 B** |
