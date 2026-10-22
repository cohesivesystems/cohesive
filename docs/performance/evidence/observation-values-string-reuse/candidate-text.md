```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean      | Error     | StdDev   | Op/s     | Gen0     | Gen1    | Allocated  |
|--------------------- |----------- |----------:|----------:|---------:|---------:|---------:|--------:|-----------:|
| **ParseDirect**          | **escaped**    |  **78.44 μs** |  **3.641 μs** | **0.946 μs** | **12,748.6** |   **7.9976** |  **1.7773** |   **67.42 KB** |
| ParseThroughDocument | escaped    |  69.61 μs |  1.317 μs | 0.204 μs | 14,364.9 |   7.8125 |  1.9531 |   64.29 KB |
| **ParseDirect**          | **large-text** |  **70.39 μs** |  **1.473 μs** | **0.382 μs** | **14,207.2** | **126.6816** | **63.3408** | **1036.53 KB** |
| ParseThroughDocument | large-text |  72.29 μs |  0.979 μs | 0.254 μs | 13,832.7 |   2.8935 |  0.2894 |   24.53 KB |
| **ParseDirect**          | **segmented**  | **130.30 μs** | **10.450 μs** | **2.714 μs** |  **7,674.3** |  **22.0287** |  **7.1721** |  **184.04 KB** |
| ParseThroughDocument | segmented  |  82.63 μs |  1.373 μs | 0.357 μs | 12,101.6 |   7.8125 |  1.9531 |    64.3 KB |
| **ParseDirect**          | **unique**     |  **31.12 μs** |  **1.822 μs** | **0.282 μs** | **32,138.2** |  **13.1903** |  **4.0680** |  **108.06 KB** |
| ParseThroughDocument | unique     |  39.48 μs |  1.281 μs | 0.333 μs | 25,330.0 |  13.1074 |  4.3159 |  108.17 KB |
