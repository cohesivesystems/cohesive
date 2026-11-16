```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-JOEEOJ : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=11  IterationTime=250ms  LaunchCount=2  
WarmupCount=5  

```
| Method   | Workload         | Mean         | Error        | StdDev       | Op/s         | Gen0   | Allocated |
|--------- |----------------- |-------------:|-------------:|-------------:|-------------:|-------:|----------:|
| **Validate** | **collection**       |  **1,362.08 ns** |    **68.916 ns** |    **79.364 ns** |    **734,169.8** | **0.0258** |     **256 B** |
| **Validate** | **flat**             |     **60.32 ns** |     **2.592 ns** |     **3.086 ns** | **16,579,591.1** | **0.0305** |     **256 B** |
| **Validate** | **large-collection** | **81,693.42 ns** | **2,104.083 ns** | **2,504.761 ns** |     **12,240.9** |      **-** |     **256 B** |
| **Validate** | **nested**           |    **367.36 ns** |    **21.282 ns** |    **26.136 ns** |  **2,722,129.2** | **0.0628** |     **536 B** |
| **Validate** | **scalar**           |     **33.33 ns** |     **0.992 ns** |     **1.219 ns** | **30,005,652.9** | **0.0305** |     **256 B** |
