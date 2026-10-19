```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method               | Items | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|--------------------- |------ |-------------:|-------------:|-----------:|------:|--------:|-------:|-------:|----------:|------------:|
| **UnpreparedSerializer** | **1**     |    **444.66 ns** |    **36.003 ns** |   **1.973 ns** |  **1.00** |    **0.01** | **0.0973** |      **-** |     **816 B** |        **1.00** |
| ColdOwner            | 1     |  1,011.17 ns |   282.217 ns |  15.469 ns |  2.27 |    0.03 | 0.2918 | 0.0057 |    2448 B |        3.00 |
| WarmOwner            | 1     |     17.63 ns |     0.282 ns |   0.015 ns |  0.04 |    0.00 |      - |      - |         - |        0.00 |
|                      |       |              |              |            |       |         |        |        |           |             |
| **UnpreparedSerializer** | **128**   | **39,439.00 ns** | **1,942.150 ns** | **106.456 ns** | **1.000** |    **0.00** | **5.5542** | **1.0986** |   **46568 B** |        **1.00** |
| ColdOwner            | 128   | 50,055.43 ns | 5,921.321 ns | 324.568 ns | 1.269 |    0.01 | 5.7373 | 0.9766 |   48206 B |        1.04 |
| WarmOwner            | 128   |     17.60 ns |     1.862 ns |   0.102 ns | 0.000 |    0.00 |      - |      - |         - |        0.00 |
