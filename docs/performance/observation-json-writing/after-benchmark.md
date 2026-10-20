```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method | Storage | Nodes | Depth | Mean          | Error        | StdDev       | Allocated |
|------- |-------- |------ |------ |--------------:|-------------:|-------------:|----------:|
| **Write**  | **ordinal** | **1**     | **1**     |      **68.37 ns** |     **25.36 ns** |     **1.390 ns** |         **-** |
| **Write**  | **ordinal** | **1**     | **32**    |   **1,021.96 ns** |    **121.00 ns** |     **6.632 ns** |         **-** |
| **Write**  | **ordinal** | **128**   | **1**     |   **5,350.88 ns** |  **1,925.98 ns** |   **105.569 ns** |         **-** |
| **Write**  | **ordinal** | **128**   | **32**    | **130,068.80 ns** | **42,980.19 ns** | **2,355.889 ns** |       **3 B** |
| **Write**  | **owned**   | **1**     | **1**     |      **86.63 ns** |     **43.75 ns** |     **2.398 ns** |         **-** |
| **Write**  | **owned**   | **1**     | **32**    |   **1,030.67 ns** |     **24.16 ns** |     **1.324 ns** |         **-** |
| **Write**  | **owned**   | **128**   | **1**     |   **7,835.28 ns** |  **2,485.38 ns** |   **136.232 ns** |         **-** |
| **Write**  | **owned**   | **128**   | **32**    | **130,812.94 ns** | **12,684.20 ns** |   **695.264 ns** |       **3 B** |
| **Write**  | **small**   | **1**     | **1**     |      **77.72 ns** |     **14.54 ns** |     **0.797 ns** |         **-** |
| **Write**  | **small**   | **1**     | **32**    |   **1,036.63 ns** |    **120.68 ns** |     **6.615 ns** |         **-** |
| **Write**  | **small**   | **128**   | **1**     |   **7,068.53 ns** |  **4,526.79 ns** |   **248.128 ns** |         **-** |
| **Write**  | **small**   | **128**   | **32**    | **128,631.85 ns** |  **3,485.51 ns** |   **191.052 ns** |       **3 B** |
