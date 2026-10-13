```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method | Count | Case  | Mean        | Error       | StdDev    | Gen0   | Gen1   | Allocated |
|------- |------ |------ |------------:|------------:|----------:|-------:|-------:|----------:|
| **Decode** | **1**     | **first** |    **633.8 ns** |    **26.79 ns** |  **11.89 ns** |      **-** |      **-** |     **528 B** |
| **Decode** | **1**     | **last**  |    **676.0 ns** |    **12.18 ns** |   **5.41 ns** |      **-** |      **-** |     **545 B** |
| **Decode** | **32**    | **first** | **19,632.0 ns** |   **339.73 ns** | **177.69 ns** | **1.7090** |      **-** |   **15161 B** |
| **Decode** | **32**    | **last**  | **20,900.7 ns** | **1,136.25 ns** | **594.28 ns** | **1.7090** |      **-** |   **15673 B** |
| **Decode** | **128**   | **first** | **79,102.8 ns** |   **737.16 ns** | **385.55 ns** | **7.0801** | **1.7090** |   **60473 B** |
| **Decode** | **128**   | **last**  | **82,055.8 ns** |   **714.39 ns** | **373.64 ns** | **7.3242** | **1.7090** |   **62521 B** |
