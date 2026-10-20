```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method   | Fields | Shape      | Mean        | Error      | StdDev    | Gen0   | Allocated |
|--------- |------- |----------- |------------:|-----------:|----------:|-------:|----------:|
| **Contract** | **1**      | **collection** |    **27.81 ns** |   **2.362 ns** |  **0.129 ns** | **0.0344** |     **288 B** |
| Value    | 1      | collection |   113.13 ns |  35.956 ns |  1.971 ns | 0.0402 |     336 B |
| **Contract** | **1**      | **flat**       |    **25.74 ns** |   **5.124 ns** |  **0.281 ns** | **0.0344** |     **288 B** |
| Value    | 1      | flat       |    53.75 ns |   5.688 ns |  0.312 ns | 0.0373 |     312 B |
| **Contract** | **1**      | **nested**     |    **25.32 ns** |   **2.305 ns** |  **0.126 ns** | **0.0344** |     **288 B** |
| Value    | 1      | nested     |    71.60 ns |   0.798 ns |  0.044 ns | 0.0372 |     312 B |
| **Contract** | **128**    | **collection** |    **25.46 ns** |   **1.108 ns** |  **0.061 ns** | **0.0344** |     **288 B** |
| Value    | 128    | collection | 9,546.41 ns | 324.728 ns | 17.799 ns | 0.7629 |    6433 B |
| **Contract** | **128**    | **flat**       |    **26.35 ns** |  **15.544 ns** |  **0.852 ns** | **0.0344** |     **288 B** |
| Value    | 128    | flat       | 2,436.15 ns | 190.052 ns | 10.417 ns | 0.4005 |    3360 B |
| **Contract** | **128**    | **nested**     |    **25.51 ns** |   **6.027 ns** |  **0.330 ns** | **0.0344** |     **288 B** |
| Value    | 128    | nested     | 4,815.75 ns | 490.846 ns | 26.905 ns | 0.3967 |    3360 B |
