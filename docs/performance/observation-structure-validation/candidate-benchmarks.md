```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method            | Fields | Shape      | Mean          | Error         | StdDev       | Gen0     | Gen1    | Gen2    | Allocated |
|------------------ |------- |----------- |--------------:|--------------:|-------------:|---------:|--------:|--------:|----------:|
| **Repeated**          | **8**      | **collection** |      **39.93 ns** |      **2.181 ns** |     **0.120 ns** |   **0.0343** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | collection |   1,755.21 ns |    311.868 ns |    17.095 ns |   1.1890 |  0.0138 |       - |    9978 B |
| **Repeated**          | **8**      | **flat**       |     **134.30 ns** |     **14.772 ns** |     **0.810 ns** |   **0.0342** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | flat       |     450.55 ns |     55.336 ns |     3.033 ns |   0.3663 |  0.0018 |       - |    3072 B |
| **Repeated**          | **8**      | **nested**     |     **299.70 ns** |    **124.819 ns** |     **6.842 ns** |   **0.0336** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | nested     |   1,057.51 ns |  1,070.665 ns |    58.687 ns |   1.0166 |  0.0122 |       - |    8513 B |
| **Repeated**          | **128**    | **collection** |      **37.54 ns** |      **0.479 ns** |     **0.026 ns** |   **0.0344** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | collection |  22,995.06 ns |  5,300.529 ns |   290.540 ns |  16.6243 |  1.6352 |       - |  139746 B |
| **Repeated**          | **128**    | **flat**       |      **37.53 ns** |      **1.990 ns** |     **0.109 ns** |   **0.0343** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | flat       |   5,946.19 ns |  1,264.538 ns |    69.314 ns |   3.5884 |  0.0475 |       - |   30166 B |
| **Repeated**          | **128**    | **nested**     |      **37.94 ns** |      **6.791 ns** |     **0.372 ns** |   **0.0343** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | nested     |  17,889.78 ns |  3,000.183 ns |   164.450 ns |  13.9828 |  0.9105 |       - |  117215 B |
| **Repeated**          | **1024**   | **collection** |      **40.08 ns** |     **23.942 ns** |     **1.312 ns** |   **0.0344** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 1024   | collection | 450,141.72 ns | 90,936.610 ns | 4,984.542 ns | 150.0000 | 71.4286 | 28.5714 | 1157054 B |
| **Repeated**          | **1024**   | **flat**       |      **38.50 ns** |      **6.865 ns** |     **0.376 ns** |   **0.0343** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 1024   | flat       |  62,381.12 ns | 43,549.145 ns | 2,387.076 ns |  29.3269 | 29.3269 | 29.3269 |  280324 B |
| **Repeated**          | **1024**   | **nested**     |      **39.03 ns** |     **21.366 ns** |     **1.171 ns** |   **0.0344** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 1024   | nested     | 362,624.07 ns | 72,108.418 ns | 3,952.506 ns | 129.2614 | 56.8182 | 26.9886 |  976743 B |
