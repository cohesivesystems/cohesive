```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean       | Error    | StdDev   | Allocated |
|--------- |----------- |-----------:|---------:|---------:|----------:|
| **Validate** | **collection** | **7,735.3 ns** | **10.19 ns** |  **4.52 ns** |         **-** |
| **Validate** | **flat**       |   **246.9 ns** | **10.93 ns** |  **5.72 ns** |       **1 B** |
| **Validate** | **large**      | **2,327.0 ns** | **75.76 ns** | **39.62 ns** |       **1 B** |
| **Validate** | **nested**     |   **413.4 ns** |  **1.18 ns** |  **0.52 ns** |       **1 B** |
