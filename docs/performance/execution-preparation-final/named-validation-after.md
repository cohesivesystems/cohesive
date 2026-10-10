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
| **Validate** | **collection** | **7,555.1 ns** | **113.24 ns** | **59.23 ns** |         **-** |
| **Validate** | **flat**       |   **241.6 ns** |   **3.36 ns** |  **1.76 ns** |       **1 B** |
| **Validate** | **large**      | **2,347.9 ns** |  **42.76 ns** | **18.99 ns** |       **1 B** |
| **Validate** | **nested**     |   **398.9 ns** |   **3.34 ns** |  **1.75 ns** |       **1 B** |
