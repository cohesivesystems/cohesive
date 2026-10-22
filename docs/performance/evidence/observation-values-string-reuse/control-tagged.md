```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method         | Workload   | Mean           | Error        | StdDev      | Op/s        | Gen0     | Gen1     | Gen2     | Allocated  |
|--------------- |----------- |---------------:|-------------:|------------:|------------:|---------:|---------:|---------:|-----------:|
| **DecodePortable** | **collection** |    **62,316.3 ns** |    **207.84 ns** |    **53.98 ns** |    **16,047.2** |  **10.4167** |        **-** |        **-** |   **86.89 KB** |
| **DecodePortable** | **flat**       |       **923.8 ns** |      **1.69 ns** |     **0.44 ns** | **1,082,430.3** |   **0.1782** |        **-** |        **-** |    **1.46 KB** |
| **DecodePortable** | **large**      | **3,133,939.1 ns** | **19,047.61 ns** | **4,946.61 ns** |       **319.1** | **362.5000** | **200.0000** | **100.0000** | **2778.25 KB** |
| **DecodePortable** | **nested**     |     **2,659.6 ns** |     **18.70 ns** |     **4.86 ns** |   **376,000.1** |   **0.5000** |        **-** |        **-** |    **4.09 KB** |
