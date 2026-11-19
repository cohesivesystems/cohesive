```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-JOEEOJ : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=11  IterationTime=250ms  LaunchCount=2  
WarmupCount=5  

```
| Method | Rejected | Mean     | Error     | StdDev    | Op/s      | Gen0   | Gen1   | Allocated |
|------- |--------- |---------:|----------:|----------:|----------:|-------:|-------:|----------:|
| **Decide** | **False**    | **2.733 μs** | **0.0389 μs** | **0.0432 μs** | **365,940.2** | **0.9649** | **0.0110** |   **7.91 KB** |
| **Decide** | **True**     | **1.339 μs** | **0.0215 μs** | **0.0256 μs** | **747,001.1** | **0.4238** |      **-** |   **3.49 KB** |
