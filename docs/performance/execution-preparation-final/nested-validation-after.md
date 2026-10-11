```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Input   | Mean        | Error     | StdDev   | Gen0   | Allocated |
|--------- |----------- |-------- |------------:|----------:|---------:|-------:|----------:|
| **Validate** | **collection** | **case**    | **29,013.8 ns** | **185.94 ns** | **97.25 ns** | **4.3945** |   **38145 B** |
| **Validate** | **collection** | **exact**   |  **9,719.5 ns** | **167.81 ns** | **74.51 ns** |      **-** |       **1 B** |
| **Validate** | **collection** | **unknown** |  **1,567.0 ns** |  **74.10 ns** | **38.76 ns** |      **-** |     **745 B** |
| **Validate** | **flat**       | **case**    |    **908.9 ns** |  **13.46 ns** |  **7.04 ns** |      **-** |    **1193 B** |
| **Validate** | **flat**       | **exact**   |    **307.0 ns** |   **8.62 ns** |  **4.51 ns** |      **-** |       **1 B** |
| **Validate** | **flat**       | **unknown** |  **1,482.1 ns** |  **68.70 ns** | **35.93 ns** |      **-** |     **361 B** |
| **Validate** | **large**      | **case**    |  **7,782.0 ns** |  **83.43 ns** | **29.75 ns** | **0.7324** |    **7121 B** |
| **Validate** | **large**      | **exact**   |  **2,955.1 ns** |  **53.17 ns** | **27.81 ns** |      **-** |       **1 B** |
| **Validate** | **large**      | **unknown** |  **7,760.3 ns** |  **48.58 ns** | **25.41 ns** |      **-** |     **361 B** |
| **Validate** | **nested**     | **case**    |  **1,060.8 ns** |   **4.81 ns** |  **2.52 ns** |      **-** |    **1193 B** |
| **Validate** | **nested**     | **exact**   |    **415.7 ns** |  **34.79 ns** | **18.20 ns** |      **-** |         **-** |
| **Validate** | **nested**     | **unknown** |  **1,476.7 ns** |  **47.79 ns** | **25.00 ns** |      **-** |    **1177 B** |
