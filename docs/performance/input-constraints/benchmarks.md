```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-UWLSOM : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=10  LaunchCount=1  WarmupCount=3

```
| Method              | Workload   | Constrained | Mean          | Error        | StdDev       | Gen0    | Gen1   | Gen2   | Allocated |
|-------------------- |----------- |------------ |--------------:|-------------:|-------------:|--------:|-------:|-------:|----------:|
| **WarmAdmission**       | **collection** | **False**       |     **602.81 ns** |    **10.345 ns** |     **5.411 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | collection | False       |   1,876.91 ns |    16.463 ns |    10.889 ns |  0.4501 | 0.0038 |      - |    3768 B |
| FreshGraphAdmission | collection | False       |   6,725.79 ns |    82.537 ns |    54.593 ns |  1.6785 | 0.0610 | 0.0305 |   14084 B |
| **WarmAdmission**       | **collection** | **True**        |     **623.35 ns** |     **6.881 ns** |     **4.095 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | collection | True        |   1,885.09 ns |    19.081 ns |    12.621 ns |  0.4501 | 0.0038 |      - |    3768 B |
| FreshGraphAdmission | collection | True        |   8,763.49 ns |    41.287 ns |    24.569 ns |  2.1515 | 0.0305 | 0.0153 |   18105 B |
| **WarmAdmission**       | **flat**       | **False**       |      **56.90 ns** |     **0.218 ns** |     **0.144 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | flat       | False       |     174.71 ns |     0.913 ns |     0.477 ns |  0.0420 |      - |      - |     352 B |
| FreshGraphAdmission | flat       | False       |   3,427.83 ns |    40.017 ns |    26.469 ns |  1.0529 |      - |      - |    8825 B |
| **WarmAdmission**       | **flat**       | **True**        |      **66.86 ns** |     **0.381 ns** |     **0.252 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | flat       | True        |     185.41 ns |     3.865 ns |     2.556 ns |  0.0420 |      - |      - |     352 B |
| FreshGraphAdmission | flat       | True        |   5,236.36 ns |   150.454 ns |    99.516 ns |  1.3733 |      - |      - |   11706 B |
| **WarmAdmission**       | **large**      | **False**       |  **36,934.85 ns** |   **148.684 ns** |    **98.345 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | large      | False       | 126,792.71 ns | 1,580.430 ns | 1,045.357 ns | 25.3906 | 7.0801 |      - |  213432 B |
| FreshGraphAdmission | large      | False       |  35,661.80 ns |   303.024 ns |   200.431 ns |  1.6479 | 0.0610 |      - |   14083 B |
| **WarmAdmission**       | **large**      | **True**        |  **35,424.00 ns** |   **388.564 ns** |   **257.011 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | large      | True        | 128,560.05 ns | 1,253.682 ns |   829.233 ns | 25.3906 | 7.0801 |      - |  213432 B |
| FreshGraphAdmission | large      | True        |  39,610.50 ns |   263.121 ns |   174.038 ns |  2.1362 | 0.0610 |      - |   18106 B |
| **WarmAdmission**       | **nested**     | **False**       |      **89.30 ns** |     **1.012 ns** |     **0.669 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | nested     | False       |     272.37 ns |     4.166 ns |     2.756 ns |  0.0629 |      - |      - |     528 B |
| FreshGraphAdmission | nested     | False       |   6,283.07 ns |    58.718 ns |    34.942 ns |  1.6785 | 0.0610 | 0.0305 |   14132 B |
| **WarmAdmission**       | **nested**     | **True**        |      **98.26 ns** |     **0.674 ns** |     **0.401 ns** |       **-** |      **-** |      **-** |         **-** |
| TypedAdmission      | nested     | True        |     268.31 ns |     3.496 ns |     2.312 ns |  0.0629 |      - |      - |     528 B |
| FreshGraphAdmission | nested     | True        |   7,745.60 ns |    65.988 ns |    39.268 ns |  2.0142 | 0.0305 |      - |   16936 B |
