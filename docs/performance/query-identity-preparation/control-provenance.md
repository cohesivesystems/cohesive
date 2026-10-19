```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method    | Count | Order   | Mean         | Error        | StdDev      | Gen0    | Gen1   | Allocated |
|---------- |------ |-------- |-------------:|-------------:|------------:|--------:|-------:|----------:|
| **Normalize** | **1**     | **reverse** |     **394.2 ns** |    **111.42 ns** |     **6.11 ns** |  **0.3213** |      **-** |   **2.63 KB** |
| **Normalize** | **1**     | **sorted**  |     **371.1 ns** |     **11.81 ns** |     **0.65 ns** |  **0.3219** |      **-** |   **2.63 KB** |
| **Normalize** | **128**   | **reverse** |  **10,690.5 ns** |  **7,895.41 ns** |   **432.77 ns** |  **3.0458** | **0.1218** |  **24.94 KB** |
| **Normalize** | **128**   | **sorted**  |   **9,026.7 ns** |    **376.72 ns** |    **20.65 ns** |  **3.0435** | **0.1087** |  **24.94 KB** |
| **Normalize** | **1024**  | **reverse** | **105,901.9 ns** | **10,201.89 ns** |   **559.20 ns** | **22.2315** | **5.4530** | **182.49 KB** |
| **Normalize** | **1024**  | **sorted**  |  **86,550.5 ns** | **39,708.14 ns** | **2,176.54 ns** | **22.2973** | **5.4054** | **182.49 KB** |
