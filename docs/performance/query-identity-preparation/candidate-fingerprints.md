```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method  | Count | Order      | Mean        | Error        | StdDev      | Gen0    | Gen1   | Allocated |
|-------- |------ |----------- |------------:|-------------:|------------:|--------:|-------:|----------:|
| **Compute** | **1**     | **duplicates** |    **256.0 ns** |    **169.89 ns** |     **9.31 ns** |  **0.0893** |      **-** |     **752 B** |
| **Compute** | **1**     | **reverse**    |    **199.4 ns** |     **24.97 ns** |     **1.37 ns** |  **0.0409** |      **-** |     **344 B** |
| **Compute** | **1**     | **sorted**     |    **200.5 ns** |     **30.30 ns** |     **1.66 ns** |  **0.0411** |      **-** |     **344 B** |
| **Compute** | **128**   | **duplicates** |  **6,890.2 ns** |    **295.18 ns** |    **16.18 ns** |  **1.2643** |      **-** |   **10672 B** |
| **Compute** | **128**   | **reverse**    |  **6,060.5 ns** |    **383.07 ns** |    **21.00 ns** |  **1.2543** |      **-** |   **10672 B** |
| **Compute** | **128**   | **sorted**     |  **3,394.4 ns** |    **143.93 ns** |     **7.89 ns** |  **0.0404** |      **-** |     **344 B** |
| **Compute** | **1024**  | **duplicates** | **70,924.5 ns** |  **1,589.10 ns** |    **87.10 ns** | **11.2613** | **1.4077** |   **94372 B** |
| **Compute** | **1024**  | **reverse**    | **65,637.9 ns** | **44,921.02 ns** | **2,462.27 ns** | **11.0597** | **1.2860** |   **94372 B** |
| **Compute** | **1024**  | **sorted**     | **24,380.3 ns** |  **3,723.97 ns** |   **204.12 ns** |       **-** |      **-** |     **345 B** |
