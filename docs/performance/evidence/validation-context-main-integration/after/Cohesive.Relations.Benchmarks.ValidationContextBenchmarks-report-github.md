```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-JOEEOJ : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=11  IterationTime=250ms  LaunchCount=2  
WarmupCount=5  

```
| Method   | Workload         | Mean         | Error      | StdDev     | Op/s         | Allocated |
|--------- |----------------- |-------------:|-----------:|-----------:|-------------:|----------:|
| **Validate** | **collection**       |  **1,367.51 ns** | **113.370 ns** | **134.959 ns** |    **731,254.1** |         **-** |
| **Validate** | **flat**             |     **55.57 ns** |   **0.862 ns** |   **1.059 ns** | **17,996,004.7** |         **-** |
| **Validate** | **large-collection** | **72,692.80 ns** | **143.491 ns** | **170.815 ns** |     **13,756.5** |         **-** |
| **Validate** | **nested**           |    **274.44 ns** |   **4.037 ns** |   **4.958 ns** |  **3,643,772.3** |         **-** |
| **Validate** | **scalar**           |     **30.77 ns** |   **0.600 ns** |   **0.737 ns** | **32,501,601.0** |         **-** |
