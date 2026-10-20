```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method  | Count | Order      | Mean        | Error       | StdDev      | Gen0    | Gen1   | Allocated |
|-------- |------ |----------- |------------:|------------:|------------:|--------:|-------:|----------:|
| **Compute** | **1**     | **duplicates** |    **258.4 ns** |    **105.8 ns** |     **5.80 ns** |  **0.0892** |      **-** |     **752 B** |
| **Compute** | **1**     | **reverse**    |    **246.6 ns** |    **129.7 ns** |     **7.11 ns** |  **0.0893** |      **-** |     **752 B** |
| **Compute** | **1**     | **sorted**     |    **273.5 ns** |    **254.1 ns** |    **13.93 ns** |  **0.0895** |      **-** |     **752 B** |
| **Compute** | **128**   | **duplicates** |  **6,707.4 ns** |    **289.9 ns** |    **15.89 ns** |  **1.2580** |      **-** |   **10672 B** |
| **Compute** | **128**   | **reverse**    |  **5,888.4 ns** |    **199.9 ns** |    **10.96 ns** |  **1.2717** |      **-** |   **10672 B** |
| **Compute** | **128**   | **sorted**     |  **5,012.5 ns** |    **407.7 ns** |    **22.35 ns** |  **1.2697** |      **-** |   **10672 B** |
| **Compute** | **1024**  | **duplicates** | **71,017.6 ns** |  **5,615.9 ns** |   **307.83 ns** | **11.2108** | **1.4013** |   **94372 B** |
| **Compute** | **1024**  | **reverse**    | **64,814.5 ns** | **32,185.0 ns** | **1,764.17 ns** | **11.1336** | **1.2652** |   **94372 B** |
| **Compute** | **1024**  | **sorted**     | **49,260.8 ns** |  **6,584.1 ns** |   **360.90 ns** | **11.1677** | **1.3715** |   **94372 B** |
