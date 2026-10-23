```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method              | Workload   | Mean           | Error         | StdDev       | Op/s        | Gen0     | Gen1     | Gen2     | Allocated  |
|-------------------- |----------- |---------------:|--------------:|-------------:|------------:|---------:|---------:|---------:|-----------:|
| **DecodePortable**      | **collection** |    **62,980.5 ns** |   **1,369.89 ns** |    **355.76 ns** |    **15,877.9** |  **10.5847** |   **0.5040** |        **-** |   **86.89 KB** |
| MaterializeResponse | collection |    61,155.5 ns |  17,876.00 ns |  4,642.34 ns |    16,351.8 | 108.9015 | 108.9015 |  40.2462 |  162.18 KB |
| **DecodePortable**      | **flat**       |       **955.8 ns** |      **10.76 ns** |      **1.66 ns** | **1,046,261.4** |   **0.1765** |        **-** |        **-** |    **1.46 KB** |
| MaterializeResponse | flat       |    42,636.0 ns |     594.85 ns |     92.05 ns |    23,454.3 | 131.7791 | 131.7791 |  37.5331 |  131.38 KB |
| **DecodePortable**      | **large**      | **3,204,977.3 ns** | **109,168.98 ns** | **28,350.84 ns** |       **312.0** | **362.5000** | **200.0000** | **100.0000** | **2777.11 KB** |
| MaterializeResponse | large      |   520,831.6 ns |  12,501.58 ns |  3,246.62 ns |     1,920.0 | 139.5833 |  52.0833 |  37.5000 |  966.93 KB |
| **DecodePortable**      | **nested**     |     **2,711.8 ns** |     **131.50 ns** |     **34.15 ns** |   **368,752.8** |   **0.4943** |        **-** |        **-** |    **4.09 KB** |
| MaterializeResponse | nested     |    36,720.7 ns |   5,825.40 ns |  1,512.84 ns |    27,232.6 | 130.1136 | 130.1136 |  37.0739 |  129.74 KB |
