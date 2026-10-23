```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method              | Workload   | Mean         | Error      | StdDev     | Op/s      | Gen0     | Gen1     | Gen2     | Allocated  |
|-------------------- |----------- |-------------:|-----------:|-----------:|----------:|---------:|---------:|---------:|-----------:|
| **DecodePortable**      | **collection** |   **149.279 μs** |  **1.3931 μs** |  **0.3618 μs** |   **6,698.8** |  **18.8107** |   **3.0340** |        **-** |  **156.23 KB** |
| MaterializeResponse | collection |    60.787 μs |  3.2252 μs |  0.4991 μs |  16,450.9 | 109.0734 | 109.0734 |  40.2992 |  162.36 KB |
| **DecodePortable**      | **flat**       |     **1.947 μs** |  **0.0107 μs** |  **0.0028 μs** | **513,612.5** |   **0.2644** |        **-** |        **-** |    **2.17 KB** |
| MaterializeResponse | flat       |    43.387 μs |  1.2145 μs |  0.3154 μs |  23,048.5 | 134.1759 | 134.1759 |  38.0886 |  133.76 KB |
| **DecodePortable**      | **large**      | **6,429.000 μs** | **83.9623 μs** | **12.9933 μs** |     **155.5** | **666.6667** | **354.1667** | **166.6667** | **5261.95 KB** |
| MaterializeResponse | large      |   550.158 μs | 10.2994 μs |  1.5938 μs |   1,817.7 | 137.9310 |  38.7931 |  36.6379 |  967.26 KB |
| **DecodePortable**      | **nested**     |     **7.123 μs** |  **0.0588 μs** |  **0.0153 μs** | **140,398.8** |   **0.8569** |        **-** |        **-** |    **7.17 KB** |
| MaterializeResponse | nested     |    35.858 μs |  2.6536 μs |  0.4106 μs |  27,887.6 | 130.3530 | 130.3530 |  37.0370 |  129.88 KB |
