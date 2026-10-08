```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean     | Error     | StdDev   | Allocated |
|------- |----------- |---------:|----------:|---------:|----------:|
| **Map**    | **collection** | **15.14 ns** | **11.551 ns** | **6.041 ns** |         **-** |
| **Map**    | **flat**       | **13.27 ns** |  **9.023 ns** | **4.719 ns** |         **-** |
| **Map**    | **large**      | **14.16 ns** |  **8.623 ns** | **4.510 ns** |         **-** |
| **Map**    | **nested**     | **13.76 ns** |  **6.866 ns** | **3.591 ns** |         **-** |
