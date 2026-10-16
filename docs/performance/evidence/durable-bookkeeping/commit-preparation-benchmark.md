```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                      | Rows | Mean             | Error            | StdDev         | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|---------------------------- |----- |-----------------:|-----------------:|---------------:|------:|--------:|--------:|-------:|----------:|------------:|
| **ConstructAndHashWholeCommit** | **1024** |  **3,018,433.76 ns** |    **57,083.273 ns** |   **3,128.927 ns** | **1.000** |    **0.00** |  **3.9063** |      **-** |   **49886 B** |        **1.00** |
| ConstructCommit             | 1024 |         52.86 ns |        12.154 ns |       0.666 ns | 0.000 |    0.00 |  0.0746 | 0.0001 |     624 B |        0.01 |
|                             |      |                  |                  |                |       |         |         |        |           |             |
| **ConstructAndHashWholeCommit** | **8192** | **24,220,839.84 ns** | **5,639,794.512 ns** | **309,136.169 ns** | **1.000** |    **0.02** | **31.2500** |      **-** |  **508962 B** |       **1.000** |
| ConstructCommit             | 8192 |         53.34 ns |         5.651 ns |       0.310 ns | 0.000 |    0.00 |  0.0746 | 0.0001 |     624 B |       0.001 |
