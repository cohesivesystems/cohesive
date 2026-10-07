```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean         | Error        | StdDev      | Gen0     | Gen1    | Gen2    | Allocated |
|------- |----------- |-------------:|-------------:|------------:|---------:|--------:|--------:|----------:|
| **Encode** | **collection** |  **36,808.2 ns** |  **1,947.55 ns** | **1,018.60 ns** |        **-** |       **-** |       **-** |   **54556 B** |
| **Encode** | **flat**       |     **440.0 ns** |     **83.39 ns** |    **43.61 ns** |        **-** |       **-** |       **-** |     **941 B** |
| **Encode** | **fractional** |   **1,650.2 ns** |     **79.12 ns** |    **35.13 ns** |        **-** |       **-** |       **-** |    **1786 B** |
| **Encode** | **large**      | **348,785.5 ns** | **17,545.50 ns** | **7,790.31 ns** | **203.1250** | **46.8750** | **31.2500** | **1865800 B** |
| **Encode** | **nested**     |   **4,411.5 ns** |    **288.51 ns** |   **150.89 ns** |        **-** |       **-** |       **-** |   **10548 B** |
