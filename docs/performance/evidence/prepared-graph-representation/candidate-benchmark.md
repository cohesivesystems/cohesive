```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms  
LaunchCount=1  WarmupCount=3  

```
| Method                   | Fields | Mean            | Error          | StdDev        | Gen0     | Gen1    | Allocated |
|------------------------- |------- |----------------:|---------------:|--------------:|---------:|--------:|----------:|
| **Project**                  | **1**      |        **23.22 ns** |       **2.064 ns** |      **0.536 ns** |        **-** |       **-** |         **-** |
| AuthorAndProjectNewGraph | 1      |     3,490.99 ns |     215.320 ns |     55.918 ns |   0.7597 |  0.0276 |    6432 B |
| DecodeNewGraphStorage    | 1      |     4,111.95 ns |     503.077 ns |    130.648 ns |   1.3182 |  0.0330 |   11127 B |
| DecodeFresh              | 1      |     1,741.39 ns |     307.994 ns |     79.985 ns |   0.4854 |  0.0202 |    4080 B |
| **Project**                  | **128**    |        **21.20 ns** |       **1.030 ns** |      **0.159 ns** |        **-** |       **-** |         **-** |
| AuthorAndProjectNewGraph | 128    |   155,262.76 ns |   9,774.332 ns |  2,538.363 ns |  22.6190 |  8.3333 |  191332 B |
| DecodeNewGraphStorage    | 128    |   136,104.52 ns |  20,037.048 ns |  3,100.755 ns |  22.5225 |  7.3198 |  188894 B |
| DecodeFresh              | 128    |    24,071.17 ns |   1,367.723 ns |    355.193 ns |   0.4650 |  0.0930 |    4077 B |
| **Project**                  | **1024**   |        **22.15 ns** |       **0.360 ns** |      **0.056 ns** |        **-** |       **-** |         **-** |
| AuthorAndProjectNewGraph | 1024   | 1,371,586.72 ns |  59,627.514 ns | 15,485.076 ns | 177.0833 | 88.5417 | 1495884 B |
| DecodeNewGraphStorage    | 1024   | 1,311,893.99 ns | 108,703.078 ns | 28,229.844 ns | 168.2692 | 81.7308 | 1443455 B |
| DecodeFresh              | 1024   |   181,366.17 ns |   4,737.945 ns |  1,230.429 ns |        - |       - |    4124 B |
