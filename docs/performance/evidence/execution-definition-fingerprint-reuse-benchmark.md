```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=1  

```
| Method                | Categories       | Shape      | Mean              | Error             | StdDev          | Ratio | RatioSD | Gen0      | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|---------------------- |----------------- |----------- |------------------:|------------------:|----------------:|------:|--------:|----------:|---------:|---------:|-----------:|------------:|
| **CreateAndRecompute**    | **AuthoredFirstUse** | **collection** |   **205,158.6100 ns** |   **217,593.2665 ns** |  **11,927.0212 ns** |  **1.00** |    **0.07** |   **63.9648** |   **8.3008** |        **-** |   **538696 B** |        **1.00** |
| CreateAndReuse        | AuthoredFirstUse | collection |   148,594.1977 ns |       893.5159 ns |      48.9766 ns |  0.73 |    0.04 |   48.3398 |   8.0566 |        - |   405289 B |        0.75 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **CreateAndRecompute**    | **AuthoredFirstUse** | **flat**       |     **2,178.6039 ns** |       **626.0634 ns** |      **34.3166 ns** |  **1.00** |    **0.02** |    **0.7324** |        **-** |        **-** |     **6136 B** |        **1.00** |
| CreateAndReuse        | AuthoredFirstUse | flat       |     1,599.8260 ns |       167.6685 ns |       9.1905 ns |  0.73 |    0.01 |    0.5398 |        - |        - |     4528 B |        0.74 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **CreateAndRecompute**    | **AuthoredFirstUse** | **large**      | **6,817,889.3151 ns** | **3,843,305.2107 ns** | **210,664.5281 ns** |  **1.00** |    **0.04** | **1945.3125** | **546.8750** | **500.0000** | **17541898 B** |        **1.00** |
| CreateAndReuse        | AuthoredFirstUse | large      | 5,416,884.5495 ns | 1,204,387.0964 ns |  66,016.5210 ns |  0.80 |    0.02 | 1414.0625 | 382.8125 | 343.7500 | 13165492 B |        0.75 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **CreateAndRecompute**    | **AuthoredFirstUse** | **nested**     |    **10,143.7376 ns** |     **2,347.8020 ns** |     **128.6909 ns** |  **1.00** |    **0.02** |    **3.6926** |   **0.0458** |        **-** |    **30984 B** |        **1.00** |
| CreateAndReuse        | AuthoredFirstUse | nested     |     8,134.7504 ns |       501.1281 ns |      27.4685 ns |  0.80 |    0.01 |    2.8534 |   0.0458 |        - |    23992 B |        0.77 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **ImportAndRecompute**    | **ImportedFirstUse** | **collection** |   **120,449.6528 ns** |    **43,322.7776 ns** |   **2,374.6676 ns** |  **1.00** |    **0.02** |   **44.6777** |   **7.4463** |        **-** |   **374665 B** |        **1.00** |
| ImportAndCompute      | ImportedFirstUse | collection |   117,601.9999 ns |    44,773.5256 ns |   2,454.1880 ns |  0.98 |    0.02 |   44.6777 |   7.3242 |        - |   374753 B |        1.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **ImportAndRecompute**    | **ImportedFirstUse** | **flat**       |     **1,294.8355 ns** |        **15.6846 ns** |       **0.8597 ns** |  **1.00** |    **0.00** |    **0.4864** |   **0.0019** |        **-** |     **4080 B** |        **1.00** |
| ImportAndCompute      | ImportedFirstUse | flat       |     1,300.2788 ns |        89.3305 ns |       4.8965 ns |  1.00 |    0.00 |    0.4978 |   0.0019 |        - |     4168 B |        1.02 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **ImportAndRecompute**    | **ImportedFirstUse** | **large**      | **4,277,549.9193 ns** | **3,104,420.9839 ns** | **170,163.7902 ns** |  **1.00** |    **0.05** | **1406.2500** | **367.1875** | **335.9375** | **12193988 B** |        **1.00** |
| ImportAndCompute      | ImportedFirstUse | large      | 4,227,864.1536 ns |   959,178.8495 ns |  52,575.8296 ns |  0.99 |    0.04 | 1265.6250 | 226.5625 | 195.3125 | 12192954 B |        1.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **ImportAndRecompute**    | **ImportedFirstUse** | **nested**     |     **6,403.0736 ns** |     **2,210.1848 ns** |     **121.1477 ns** |  **1.00** |    **0.02** |    **2.6779** |   **0.0610** |        **-** |    **22440 B** |        **1.00** |
| ImportAndCompute      | ImportedFirstUse | nested     |     6,368.9944 ns |     1,018.2472 ns |      55.8136 ns |  0.99 |    0.02 |    2.6855 |   0.0687 |        - |    22528 B |        1.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **RecomputeWarmDocument** | **Warm**             | **collection** |    **42,282.9098 ns** |     **7,559.5825 ns** |     **414.3662 ns** | **1.000** |    **0.01** |   **15.9302** |   **1.0986** |        **-** |   **133408 B** |        **1.00** |
| ReuseWarmDocument     | Warm             | collection |         0.1736 ns |         0.3357 ns |       0.0184 ns | 0.000 |    0.00 |         - |        - |        - |          - |        0.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **RecomputeWarmDocument** | **Warm**             | **flat**       |       **574.1034 ns** |        **88.2560 ns** |       **4.8376 ns** | **1.000** |    **0.01** |    **0.1917** |        **-** |        **-** |     **1608 B** |        **1.00** |
| ReuseWarmDocument     | Warm             | flat       |         0.1982 ns |         0.0352 ns |       0.0019 ns | 0.000 |    0.00 |         - |        - |        - |          - |        0.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **RecomputeWarmDocument** | **Warm**             | **large**      | **1,786,133.8151 ns** |   **256,972.1064 ns** |  **14,085.5083 ns** | **1.000** |    **0.01** |  **529.2969** | **173.8281** | **158.2031** |  **4376268 B** |        **1.00** |
| ReuseWarmDocument     | Warm             | large      |         0.2471 ns |         0.2593 ns |       0.0142 ns | 0.000 |    0.00 |         - |        - |        - |          - |        0.00 |
|                       |                  |            |                   |                   |                 |       |         |           |          |          |            |             |
| **RecomputeWarmDocument** | **Warm**             | **nested**     |     **1,917.1082 ns** |       **158.9398 ns** |       **8.7120 ns** | **1.000** |    **0.01** |    **0.8354** |   **0.0114** |        **-** |     **6992 B** |        **1.00** |
| ReuseWarmDocument     | Warm             | nested     |         0.2077 ns |         0.1088 ns |       0.0060 ns | 0.000 |    0.00 |         - |        - |        - |          - |        0.00 |
