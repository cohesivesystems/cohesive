```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Input   | Mean         | Error       | StdDev      | Gen0    | Allocated |
|--------- |----------- |-------- |-------------:|------------:|------------:|--------:|----------:|
| **Validate** | **collection** | **case**    | **218,830.6 ns** | **3,678.24 ns** | **1,923.79 ns** | **21.4844** |  **180224 B** |
| **Validate** | **collection** | **exact**   |  **10,839.5 ns** |   **417.88 ns** |   **218.56 ns** |       **-** |       **1 B** |
| **Validate** | **collection** | **unknown** |   **1,974.8 ns** |    **82.80 ns** |    **43.31 ns** |       **-** |     **745 B** |
| **Validate** | **flat**       | **case**    |   **5,087.1 ns** |    **58.59 ns** |    **20.89 ns** |  **0.4883** |    **5633 B** |
| **Validate** | **flat**       | **exact**   |     **301.5 ns** |    **11.96 ns** |     **6.25 ns** |       **-** |       **1 B** |
| **Validate** | **flat**       | **unknown** |   **1,896.8 ns** |    **70.55 ns** |    **36.90 ns** |       **-** |     **361 B** |
| **Validate** | **large**      | **case**    | **278,593.6 ns** | **9,822.87 ns** | **5,137.55 ns** |  **5.3711** |   **45057 B** |
| **Validate** | **large**      | **exact**   |   **2,818.5 ns** |    **27.64 ns** |    **14.46 ns** |       **-** |       **1 B** |
| **Validate** | **large**      | **unknown** |  **30,739.2 ns** |   **506.45 ns** |   **224.87 ns** |       **-** |     **361 B** |
| **Validate** | **nested**     | **case**    |   **5,727.3 ns** |   **306.01 ns** |   **160.05 ns** |  **0.4883** |    **5633 B** |
| **Validate** | **nested**     | **exact**   |     **572.4 ns** |    **23.73 ns** |    **12.41 ns** |       **-** |       **1 B** |
| **Validate** | **nested**     | **unknown** |   **2,091.2 ns** |    **23.94 ns** |    **12.52 ns** |       **-** |    **1177 B** |
