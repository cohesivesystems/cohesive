```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method               | Shape      | Mean           | Error          | StdDev        | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|--------------------- |----------- |---------------:|---------------:|--------------:|------:|--------:|---------:|---------:|---------:|-----------:|------------:|
| **MutableNodeReference** | **collection** |    **73,973.7 ns** |    **82,405.7 ns** |   **4,516.94 ns** |  **1.00** |    **0.08** |  **21.9727** |   **5.3711** |        **-** |   **180.2 KB** |        **1.00** |
| ImmutableDocument    | collection |    54,969.7 ns |     9,005.2 ns |     493.61 ns |  0.74 |    0.04 |   7.1411 |   0.2441 |        - |   58.52 KB |        0.32 |
|                      |            |                |                |               |       |         |          |          |          |            |             |
| **MutableNodeReference** | **flat**       |       **937.6 ns** |       **141.5 ns** |       **7.76 ns** |  **1.00** |    **0.01** |   **0.3281** |        **-** |        **-** |     **2.7 KB** |        **1.00** |
| ImmutableDocument    | flat       |       789.7 ns |       619.2 ns |      33.94 ns |  0.84 |    0.03 |   0.1268 |        - |        - |    1.04 KB |        0.38 |
|                      |            |                |                |               |       |         |          |          |          |            |             |
| **MutableNodeReference** | **large**      | **3,179,123.2 ns** | **5,499,925.9 ns** | **301,469.50 ns** |  **1.01** |    **0.11** | **851.5625** | **546.8750** | **296.8750** | **5826.82 KB** |        **1.00** |
| ImmutableDocument    | large      | 1,958,830.4 ns | 3,462,670.1 ns | 189,800.63 ns |  0.62 |    0.07 | 312.5000 | 156.2500 | 148.4375 |  1980.9 KB |        0.34 |
|                      |            |                |                |               |       |         |          |          |          |            |             |
| **MutableNodeReference** | **nested**     |    **16,408.8 ns** |     **1,678.0 ns** |      **91.97 ns** |  **1.00** |    **0.01** |   **5.8594** |   **0.4883** |        **-** |    **47.9 KB** |        **1.00** |
| ImmutableDocument    | nested     |    14,385.9 ns |    68,874.3 ns |   3,775.23 ns |  0.88 |    0.20 |   2.5024 |        - |        - |   20.57 KB |        0.43 |
