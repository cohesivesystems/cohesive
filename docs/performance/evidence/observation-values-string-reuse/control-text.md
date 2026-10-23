```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean      | Error    | StdDev   | Op/s     | Gen0     | Gen1    | Allocated  |
|--------------------- |----------- |----------:|---------:|---------:|---------:|---------:|--------:|-----------:|
| **ParseDirect**          | **escaped**    |  **71.65 μs** | **0.333 μs** | **0.087 μs** | **13,956.8** |  **14.0482** |  **4.5872** |  **116.04 KB** |
| ParseThroughDocument | escaped    |  67.88 μs | 3.523 μs | 0.915 μs | 14,731.6 |   7.8125 |  1.8858 |   64.29 KB |
| **ParseDirect**          | **large-text** |  **70.17 μs** | **0.836 μs** | **0.129 μs** | **14,251.8** | **126.7123** | **63.3562** | **1036.53 KB** |
| ParseThroughDocument | large-text |  73.02 μs | 0.231 μs | 0.060 μs | 13,695.7 |   2.9206 |  0.2921 |   24.53 KB |
| **ParseDirect**          | **segmented**  | **129.54 μs** | **0.416 μs** | **0.108 μs** |  **7,719.8** |  **22.0287** |  **7.1721** |  **184.04 KB** |
| ParseThroughDocument | segmented  |  78.82 μs | 0.683 μs | 0.177 μs | 12,686.9 |   7.5758 |  1.8939 |   64.29 KB |
| **ParseDirect**          | **unique**     |  **29.37 μs** | **0.132 μs** | **0.034 μs** | **34,052.4** |  **13.1086** |  **4.3305** |  **108.03 KB** |
| ParseThroughDocument | unique     |  38.86 μs | 1.062 μs | 0.276 μs | 25,733.1 |  13.2064 |  4.2998 |  108.17 KB |
