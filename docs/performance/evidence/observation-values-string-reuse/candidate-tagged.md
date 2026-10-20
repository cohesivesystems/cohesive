```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method         | Workload   | Mean           | Error        | StdDev      | Op/s        | Gen0     | Gen1    | Gen2    | Allocated  |
|--------------- |----------- |---------------:|-------------:|------------:|------------:|---------:|--------:|--------:|-----------:|
| **DecodePortable** | **collection** |    **62,030.5 ns** |    **563.19 ns** |   **146.26 ns** |    **16,121.1** |   **4.5000** |  **0.2500** |       **-** |   **38.53 KB** |
| **DecodePortable** | **flat**       |       **911.5 ns** |      **8.40 ns** |     **1.30 ns** | **1,097,059.9** |   **0.1783** |       **-** |       **-** |    **1.46 KB** |
| **DecodePortable** | **large**      | **2,332,698.8 ns** | **34,316.15 ns** | **8,911.79 ns** |       **428.7** | **142.8571** | **80.3571** | **44.6429** | **1135.11 KB** |
| **DecodePortable** | **nested**     |     **2,639.8 ns** |    **155.47 ns** |    **40.38 ns** |   **378,820.0** |   **0.4939** |       **-** |       **-** |    **4.09 KB** |
