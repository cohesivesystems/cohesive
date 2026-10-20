```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method  | Rows | Mean         | Error         | StdDev     | Gen0     | Gen1    | Allocated  |
|-------- |----- |-------------:|--------------:|-----------:|---------:|--------:|-----------:|
| **Project** | **1**    |     **3.162 μs** |     **0.7039 μs** |  **0.0386 μs** |   **0.2486** |       **-** |    **2.11 KB** |
| **Project** | **128**  |   **392.385 μs** |    **63.9127 μs** |  **3.5033 μs** |  **28.9634** |  **6.0976** |  **237.29 KB** |
| **Project** | **1024** | **3,351.301 μs** | **1,160.4792 μs** | **63.6098 μs** | **225.0000** | **87.5000** | **1896.49 KB** |
