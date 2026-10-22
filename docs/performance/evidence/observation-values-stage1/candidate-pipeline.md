```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean           | Error         | StdDev       | Op/s        | Gen0     | Gen1     | Gen2    | Allocated  |
|--------------------- |----------- |---------------:|--------------:|-------------:|------------:|---------:|---------:|--------:|-----------:|
| **ParseDirect**          | **collection** |    **30,475.6 ns** |     **129.58 ns** |     **20.05 ns** |    **32,813.1** |   **7.1881** |   **1.5838** |       **-** |   **59.15 KB** |
| ParseThroughDocument | collection |    36,654.3 ns |     980.40 ns |    254.61 ns |    27,282.0 |   5.0000 |   1.0294 |       - |   41.43 KB |
| ParseMapWrite        | collection |    35,307.5 ns |     352.69 ns |     54.58 ns |    28,322.6 |   7.2115 |   0.5656 |       - |   59.36 KB |
| **ParseDirect**          | **flat**       |       **611.3 ns** |       **9.91 ns** |      **1.53 ns** | **1,635,887.8** |   **0.1988** |        **-** |       **-** |    **1.64 KB** |
| ParseThroughDocument | flat       |     1,366.5 ns |      38.78 ns |     10.07 ns |   731,800.4 |   0.2070 |        - |       - |    1.71 KB |
| ParseMapWrite        | flat       |       717.4 ns |       7.11 ns |      1.10 ns | 1,393,999.1 |   0.2303 |        - |       - |    1.89 KB |
| **ParseDirect**          | **large**      | **1,857,681.9 ns** | **317,238.46 ns** | **49,093.00 ns** |       **538.3** | **250.0000** | **138.8889** | **69.4444** | **1901.72 KB** |
| ParseThroughDocument | large      | 1,727,018.0 ns | 348,988.97 ns | 90,631.33 ns |       579.0 | 175.0000 | 100.0000 | 62.5000 | 1316.48 KB |
| ParseMapWrite        | large      | 1,840,161.0 ns |  39,388.16 ns |  6,095.36 ns |       543.4 | 256.9444 | 166.6667 | 83.3333 | 1909.71 KB |
| **ParseDirect**          | **nested**     |     **1,736.3 ns** |       **4.16 ns** |      **1.08 ns** |   **575,932.6** |   **0.5624** |   **0.0139** |       **-** |    **4.61 KB** |
| ParseThroughDocument | nested     |     3,550.4 ns |      96.73 ns |     25.12 ns |   281,659.3 |   0.5674 |   0.0142 |       - |    4.71 KB |
| ParseMapWrite        | nested     |     2,085.8 ns |      22.94 ns |      5.96 ns |   479,422.7 |   0.5824 |        - |       - |    4.82 KB |
