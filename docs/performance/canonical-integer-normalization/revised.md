```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean         | Error        | StdDev       | Gen0    | Gen1    | Gen2    | Allocated |
|------- |----------- |-------------:|-------------:|-------------:|--------:|--------:|--------:|----------:|
| **Encode** | **collection** |   **7,248.5 ns** |    **426.92 ns** |    **223.29 ns** |       **-** |       **-** |       **-** |    **7484 B** |
| **Encode** | **flat**       |     **281.9 ns** |     **31.42 ns** |     **16.43 ns** |       **-** |       **-** |       **-** |     **573 B** |
| **Encode** | **fractional** |   **2,181.6 ns** |    **203.83 ns** |    **106.61 ns** |       **-** |       **-** |       **-** |    **1781 B** |
| **Encode** | **large**      | **216,139.5 ns** | **33,351.20 ns** | **14,808.14 ns** | **31.2500** | **31.2500** | **31.2500** |  **359009 B** |
| **Encode** | **nested**     |   **5,511.4 ns** |    **241.73 ns** |    **107.33 ns** |       **-** |       **-** |       **-** |   **10191 B** |
