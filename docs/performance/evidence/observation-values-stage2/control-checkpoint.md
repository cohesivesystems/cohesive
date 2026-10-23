```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method         | Workload   | Mean      | Error     | StdDev    | Op/s   | Gen0      | Gen1     | Gen2     | Allocated |
|--------------- |----------- |----------:|----------:|----------:|-------:|----------:|---------:|---------:|----------:|
| **Encode**         | **collection** |  **3.700 ms** | **0.2946 ms** | **0.0765 ms** | **270.24** |  **312.5000** | **156.2500** |        **-** |   **2.62 MB** |
| DecodeAndAdmit | collection |  4.338 ms | 0.1491 ms | 0.0231 ms | 230.51 |  343.7500 | 171.8750 |        - |   2.84 MB |
| **Encode**         | **flat**       |  **3.242 ms** | **0.1545 ms** | **0.0401 ms** | **308.44** |  **312.5000** | **150.0000** |        **-** |   **2.51 MB** |
| DecodeAndAdmit | flat       |  3.341 ms | 0.2258 ms | 0.0586 ms | 299.28 |  312.5000 | 150.0000 |        - |   2.55 MB |
| **Encode**         | **large**      | **14.055 ms** | **0.5149 ms** | **0.0797 ms** |  **71.15** |  **937.5000** | **781.2500** | **500.0000** |   **7.96 MB** |
| DecodeAndAdmit | large      | 36.870 ms | 2.0252 ms | 0.3134 ms |  27.12 | 1428.5714 | 857.1429 | 571.4286 |  14.62 MB |
| **Encode**         | **nested**     |  **3.429 ms** | **0.0754 ms** | **0.0196 ms** | **291.66** |  **312.5000** | **150.0000** |        **-** |   **2.51 MB** |
| DecodeAndAdmit | nested     |  3.666 ms | 0.3985 ms | 0.1035 ms | 272.80 |  312.5000 | 150.0000 |        - |   2.57 MB |
