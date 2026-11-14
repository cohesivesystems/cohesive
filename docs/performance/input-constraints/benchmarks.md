```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-FGEKWY : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=3  LaunchCount=1  WarmupCount=1  

```
| Method              | Workload   | Constrained | Mean         | Error          | StdDev        | Gen0   | Gen1   | Gen2   | Allocated |
|-------------------- |----------- |------------ |-------------:|---------------:|--------------:|-------:|-------:|-------:|----------:|
| **WarmAdmission**       | **collection** | **False**       |    **569.70 ns** |      **54.736 ns** |      **3.000 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | collection | False       | 13,293.46 ns |  79,625.804 ns |  4,364.559 ns | 1.6479 | 0.0610 |      - |   14083 B |
| **WarmAdmission**       | **collection** | **True**        |    **674.71 ns** |     **124.661 ns** |      **6.833 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | collection | True        | 10,623.90 ns |  34,964.302 ns |  1,916.511 ns | 2.1362 | 0.0610 |      - |   18058 B |
| **WarmAdmission**       | **flat**       | **False**       |     **53.57 ns** |      **15.853 ns** |      **0.869 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | flat       | False       |  3,499.65 ns |     240.306 ns |     13.172 ns | 1.0529 |      - |      - |    8825 B |
| **WarmAdmission**       | **flat**       | **True**        |     **57.86 ns** |       **1.959 ns** |      **0.107 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | flat       | True        |  5,668.24 ns |  26,107.151 ns |  1,431.021 ns | 1.3733 |      - |      - |   11706 B |
| **WarmAdmission**       | **large**      | **False**       | **34,628.45 ns** |     **685.329 ns** |     **37.565 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | large      | False       | 33,358.56 ns |     325.692 ns |     17.852 ns | 1.6479 | 0.0610 |      - |   14083 B |
| **WarmAdmission**       | **large**      | **True**        | **43,675.75 ns** |   **2,878.340 ns** |    **157.772 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | large      | True        | 64,673.60 ns | 323,252.705 ns | 17,718.572 ns | 1.9531 | 0.4883 |      - |   18060 B |
| **WarmAdmission**       | **nested**     | **False**       |    **100.43 ns** |     **261.953 ns** |     **14.359 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | nested     | False       |  8,262.45 ns |  38,154.132 ns |  2,091.357 ns | 1.6785 | 0.0610 | 0.0305 |   14132 B |
| **WarmAdmission**       | **nested**     | **True**        |    **104.14 ns** |      **75.248 ns** |      **4.125 ns** |      **-** |      **-** |      **-** |         **-** |
| FreshGraphAdmission | nested     | True        | 15,408.83 ns | 100,420.088 ns |  5,504.364 ns | 2.0142 | 0.0610 |      - |   16936 B |
