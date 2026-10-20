```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method    | Count | Order   | Mean         | Error        | StdDev     | Gen0   | Allocated |
|---------- |------ |-------- |-------------:|-------------:|-----------:|-------:|----------:|
| **Normalize** | **1**     | **reverse** |     **16.15 ns** |     **0.224 ns** |   **0.012 ns** | **0.0114** |      **96 B** |
| **Normalize** | **1**     | **sorted**  |     **16.43 ns** |     **4.696 ns** |   **0.257 ns** | **0.0114** |      **96 B** |
| **Normalize** | **128**   | **reverse** |  **3,638.53 ns** |   **226.192 ns** |  **12.398 ns** | **0.2477** |    **2192 B** |
| **Normalize** | **128**   | **sorted**  |    **463.01 ns** |   **105.613 ns** |   **5.789 ns** | **0.0100** |      **96 B** |
| **Normalize** | **1024**  | **reverse** | **44,808.59 ns** | **6,584.071 ns** | **360.895 ns** | **1.9699** |   **16528 B** |
| **Normalize** | **1024**  | **sorted**  |  **3,200.39 ns** |   **825.221 ns** |  **45.233 ns** |      **-** |      **96 B** |
