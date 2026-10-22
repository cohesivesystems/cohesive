```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean           | Error         | StdDev       | Op/s        | Gen0     | Gen1     | Gen2     | Allocated  |
|--------------------- |----------- |---------------:|--------------:|-------------:|------------:|---------:|---------:|---------:|-----------:|
| **ParseDirect**          | **collection** |    **72,190.5 ns** |   **3,229.26 ns** |    **838.63 ns** |    **13,852.2** |  **11.8778** |   **3.9593** |        **-** |   **97.39 KB** |
| ParseThroughDocument | collection |    39,155.2 ns |   1,073.46 ns |    278.77 ns |    25,539.4 |   5.1692 |   0.9398 |        - |   43.41 KB |
| ParseMapWrite        | collection |    80,481.1 ns |  12,522.91 ns |  3,252.16 ns |    12,425.3 |  13.6139 |   2.7847 |        - |  111.74 KB |
| **ParseDirect**          | **flat**       |       **845.8 ns** |      **12.93 ns** |      **2.00 ns** | **1,182,318.5** |   **0.1976** |        **-** |        **-** |    **1.64 KB** |
| ParseThroughDocument | flat       |     1,389.0 ns |      61.49 ns |      9.52 ns |   719,965.7 |   0.2085 |        - |        - |    1.71 KB |
| ParseMapWrite        | flat       |     1,060.6 ns |      15.25 ns |      3.96 ns |   942,857.8 |   0.2478 |        - |        - |    2.05 KB |
| **ParseDirect**          | **large**      | **3,611,618.5 ns** | **111,316.82 ns** | **28,908.62 ns** |       **276.9** | **425.0000** | **237.5000** | **112.5000** | **3105.62 KB** |
| ParseThroughDocument | large      | 1,716,772.7 ns |  76,968.06 ns | 11,910.89 ns |       582.5 | 181.2500 | 106.2500 |  62.5000 | 1380.27 KB |
| ParseMapWrite        | large      | 3,666,492.4 ns |  81,578.16 ns | 21,185.59 ns |       272.7 | 462.5000 | 262.5000 | 125.0000 | 3554.34 KB |
| **ParseDirect**          | **nested**     |    **15,273.5 ns** |   **2,760.31 ns** |    **716.84 ns** |    **65,472.9** |   **1.4454** |   **0.1156** |        **-** |    **11.9 KB** |
| ParseThroughDocument | nested     |     3,704.3 ns |     878.64 ns |    228.18 ns |   269,960.1 |   0.5632 |   0.0141 |        - |    4.71 KB |
| ParseMapWrite        | nested     |    15,069.7 ns |     620.39 ns |    161.11 ns |    66,358.5 |   1.7943 |   0.0598 |        - |   14.85 KB |
