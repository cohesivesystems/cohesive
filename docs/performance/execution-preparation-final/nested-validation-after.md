```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Input   | Mean        | Error     | StdDev    | Gen0   | Allocated |
|--------- |----------- |-------- |------------:|----------:|----------:|-------:|----------:|
| **Validate** | **collection** | **case**    | **28,833.6 ns** | **196.92 ns** |  **87.43 ns** | **4.3945** |   **38145 B** |
| **Validate** | **collection** | **exact**   |  **9,705.0 ns** | **151.60 ns** |  **67.31 ns** |      **-** |         **-** |
| **Validate** | **collection** | **unknown** |  **1,593.0 ns** |  **36.98 ns** |  **16.42 ns** |      **-** |     **745 B** |
| **Validate** | **flat**       | **case**    |    **884.5 ns** |   **5.60 ns** |   **2.93 ns** |      **-** |    **1193 B** |
| **Validate** | **flat**       | **exact**   |    **313.7 ns** |  **11.00 ns** |   **4.88 ns** |      **-** |       **1 B** |
| **Validate** | **flat**       | **unknown** |  **1,509.9 ns** |  **54.32 ns** |  **28.41 ns** |      **-** |     **361 B** |
| **Validate** | **large**      | **case**    |  **8,020.4 ns** | **270.78 ns** | **141.63 ns** | **0.7324** |    **7121 B** |
| **Validate** | **large**      | **exact**   |  **2,911.7 ns** |  **46.22 ns** |  **24.18 ns** |      **-** |       **1 B** |
| **Validate** | **large**      | **unknown** |  **7,777.0 ns** | **241.96 ns** | **126.55 ns** |      **-** |     **361 B** |
| **Validate** | **nested**     | **case**    |  **1,033.2 ns** |   **2.78 ns** |   **1.24 ns** |      **-** |    **1193 B** |
| **Validate** | **nested**     | **exact**   |    **413.7 ns** |  **17.26 ns** |   **7.66 ns** |      **-** |       **1 B** |
| **Validate** | **nested**     | **unknown** |  **1,498.4 ns** |  **58.67 ns** |  **30.68 ns** |      **-** |    **1177 B** |
