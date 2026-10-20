```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method            | Fields | Shape      | Mean          | Error            | StdDev        | Gen0     | Gen1    | Gen2    | Allocated |
|------------------ |------- |----------- |--------------:|-----------------:|--------------:|---------:|--------:|--------:|----------:|
| **Repeated**          | **8**      | **collection** |     **382.73 ns** |        **77.956 ns** |      **4.273 ns** |   **0.0337** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | collection |   1,246.66 ns |        51.087 ns |      2.800 ns |   1.1838 |  0.0203 |       - |    9921 B |
| **Repeated**          | **8**      | **flat**       |      **84.30 ns** |         **5.977 ns** |      **0.328 ns** |   **0.0344** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | flat       |     403.08 ns |        37.913 ns |      2.078 ns |   0.3671 |  0.0016 |       - |    3072 B |
| **Repeated**          | **8**      | **nested**     |     **172.09 ns** |        **20.582 ns** |      **1.128 ns** |   **0.0344** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 8      | nested     |     920.57 ns |        13.607 ns |      0.746 ns |   1.0156 |  0.0119 |       - |    8513 B |
| **Repeated**          | **128**    | **collection** |   **4,625.14 ns** |     **1,062.906 ns** |     **58.261 ns** |   **0.0189** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | collection |  19,215.70 ns |       887.668 ns |     48.656 ns |  16.6565 |  2.7506 |       - |  139693 B |
| **Repeated**          | **128**    | **flat**       |     **737.57 ns** |        **23.172 ns** |      **1.270 ns** |   **0.0324** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | flat       |   5,177.48 ns |       196.589 ns |     10.776 ns |   3.5844 |  0.2472 |       - |   30109 B |
| **Repeated**          | **128**    | **nested**     |   **2,213.22 ns** |       **120.649 ns** |      **6.613 ns** |   **0.0269** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 128    | nested     |  14,511.72 ns |     1,160.185 ns |     63.594 ns |  13.9580 |  2.0909 |       - |  117162 B |
| **Repeated**          | **1024**   | **collection** |  **48,323.47 ns** |    **30,979.795 ns** |  **1,698.107 ns** |        **-** |       **-** |       **-** |     **289 B** |
| CreateAndValidate | 1024   | collection | 475,314.59 ns | 1,326,241.930 ns | 72,695.796 ns | 146.9595 | 72.6351 | 30.4054 | 1157020 B |
| **Repeated**          | **1024**   | **flat**       |   **5,735.15 ns** |       **271.050 ns** |     **14.857 ns** |   **0.0230** |       **-** |       **-** |     **288 B** |
| CreateAndValidate | 1024   | flat       |  56,950.63 ns |    25,414.296 ns |  1,393.043 ns |  29.2266 | 29.2266 | 29.2266 |  280271 B |
| **Repeated**          | **1024**   | **nested**     |  **23,302.25 ns** |     **2,757.437 ns** |    **151.144 ns** |        **-** |       **-** |       **-** |     **289 B** |
| CreateAndValidate | 1024   | nested     | 352,855.22 ns |    24,531.844 ns |  1,344.673 ns | 130.5556 | 56.9444 | 27.7778 |  976720 B |
