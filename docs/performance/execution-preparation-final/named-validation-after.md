```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Shape      | Mean       | Error     | StdDev   | Allocated |
|--------- |----------- |-----------:|----------:|---------:|----------:|
| **Validate** | **collection** | **8,008.5 ns** | **159.12 ns** | **83.22 ns** |         **-** |
| **Validate** | **flat**       |   **255.1 ns** |  **18.22 ns** |  **9.53 ns** |       **1 B** |
| **Validate** | **large**      | **2,396.8 ns** |  **71.10 ns** | **37.19 ns** |       **1 B** |
| **Validate** | **nested**     |   **416.2 ns** |  **23.39 ns** | **12.23 ns** |       **1 B** |
