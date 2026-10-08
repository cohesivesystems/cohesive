```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Shape      | Mean          | Error       | StdDev      | Allocated |
|------- |----------- |--------------:|------------:|------------:|----------:|
| **Map**    | **collection** |  **38,934.49 ns** | **1,588.53 ns** |   **830.83 ns** |   **23838 B** |
| **Map**    | **flat**       |      **97.52 ns** |    **26.24 ns** |    **11.65 ns** |     **192 B** |
| **Map**    | **large**      | **136,775.97 ns** | **9,426.66 ns** | **4,930.33 ns** |   **88043 B** |
| **Map**    | **nested**     |  **20,652.18 ns** |   **982.11 ns** |   **513.66 ns** |   **10929 B** |
