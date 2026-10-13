```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Count | Case  | Mean        | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|------- |------ |------ |------------:|----------:|----------:|-------:|-------:|----------:|
| **Decode** | **1**     | **first** |    **637.9 ns** |  **27.33 ns** |  **14.30 ns** |      **-** |      **-** |     **530 B** |
| **Decode** | **1**     | **last**  |    **690.0 ns** |  **50.92 ns** |  **26.63 ns** |      **-** |      **-** |     **546 B** |
| **Decode** | **32**    | **first** | **19,160.6 ns** | **226.61 ns** | **118.52 ns** | **1.7090** |      **-** |   **15162 B** |
| **Decode** | **32**    | **last**  | **20,462.7 ns** | **570.68 ns** | **298.47 ns** | **1.7090** |      **-** |   **15674 B** |
| **Decode** | **128**   | **first** | **77,301.9 ns** | **641.74 ns** | **335.64 ns** | **7.0801** | **1.7090** |   **60474 B** |
| **Decode** | **128**   | **last**  | **82,142.8 ns** | **540.64 ns** | **240.05 ns** | **7.3242** | **1.7090** |   **62521 B** |
