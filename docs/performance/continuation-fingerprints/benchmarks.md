```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | Categories     | Shape      | Mean             | Error             | StdDev          | Ratio | RatioSD | Gen0      | Gen1     | Gen2     | Allocated | Alloc Ratio |
|------------------ |--------------- |----------- |-----------------:|------------------:|----------------:|------:|--------:|----------:|---------:|---------:|----------:|------------:|
| **FreshReference**    | **fresh snapshot** | **collection** |   **170,562.907 ns** |   **222,875.9092 ns** |  **12,216.5807 ns** |  **1.00** |    **0.09** |   **35.6445** |   **1.9531** |        **-** |  **301440 B** |        **1.00** |
| FreshMemoized     | fresh snapshot | collection |   165,192.179 ns |    45,264.9077 ns |   2,481.1223 ns |  0.97 |    0.06 |   35.8887 |   1.7090 |        - |  301627 B |        1.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **FreshReference**    | **fresh snapshot** | **flat**       |     **8,461.355 ns** |     **2,802.5098 ns** |     **153.6150 ns** |  **1.00** |    **0.02** |    **1.5869** |        **-** |        **-** |   **13302 B** |        **1.00** |
| FreshMemoized     | fresh snapshot | flat       |     9,002.511 ns |     6,051.4398 ns |     331.6998 ns |  1.06 |    0.04 |    1.5869 |   0.0610 |        - |   13494 B |        1.01 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **FreshReference**    | **fresh snapshot** | **large**      | **5,948,845.490 ns** | **1,724,563.5108 ns** |  **94,529.1457 ns** |  **1.00** |    **0.02** | **1156.2500** | **343.7500** | **343.7500** | **9728229 B** |        **1.00** |
| FreshMemoized     | fresh snapshot | large      | 6,191,232.833 ns | 6,484,135.7496 ns | 355,417.3616 ns |  1.04 |    0.05 | 1156.2500 | 343.7500 | 343.7500 | 9727800 B |        1.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **FreshReference**    | **fresh snapshot** | **nested**     |    **21,716.336 ns** |    **15,539.3331 ns** |     **851.7633 ns** |  **1.00** |    **0.05** |    **4.7607** |        **-** |        **-** |   **40144 B** |        **1.00** |
| FreshMemoized     | fresh snapshot | nested     |    21,522.917 ns |    12,085.5340 ns |     662.4489 ns |  0.99 |    0.04 |    4.7607 |   0.1221 |        - |   40330 B |        1.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **RepeatedReference** | **same snapshot**  | **collection** |   **150,846.356 ns** |   **131,553.2972 ns** |   **7,210.8802 ns** | **1.001** |    **0.06** |   **24.1699** |   **2.6855** |        **-** |  **202997 B** |        **1.00** |
| RepeatedMemoized  | same snapshot  | collection |         3.115 ns |         2.0746 ns |       0.1137 ns | 0.000 |    0.00 |         - |        - |        - |         - |        0.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **RepeatedReference** | **same snapshot**  | **flat**       |     **6,241.012 ns** |       **712.8877 ns** |      **39.0758 ns** | **1.000** |    **0.01** |    **1.2817** |        **-** |        **-** |   **10772 B** |        **1.00** |
| RepeatedMemoized  | same snapshot  | flat       |         3.024 ns |         0.8835 ns |       0.0484 ns | 0.000 |    0.00 |         - |        - |        - |         - |        0.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **RepeatedReference** | **same snapshot**  | **large**      | **5,596,314.234 ns** |   **842,033.4048 ns** |  **46,154.6924 ns** | **1.000** |    **0.01** |  **812.5000** | **375.0000** | **375.0000** | **6459106 B** |        **1.00** |
| RepeatedMemoized  | same snapshot  | large      |         3.268 ns |         1.1929 ns |       0.0654 ns | 0.000 |    0.00 |         - |        - |        - |         - |        0.00 |
|                   |                |            |                  |                   |                 |       |         |           |          |          |           |             |
| **RepeatedReference** | **same snapshot**  | **nested**     |    **19,150.869 ns** |    **17,743.5609 ns** |     **972.5845 ns** | **1.002** |    **0.06** |    **2.6855** |        **-** |        **-** |   **23016 B** |        **1.00** |
| RepeatedMemoized  | same snapshot  | nested     |         3.244 ns |         0.2910 ns |       0.0159 ns | 0.000 |    0.00 |         - |        - |        - |         - |        0.00 |
