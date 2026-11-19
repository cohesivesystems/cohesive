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
| **Decide** | **False**    | **2.724 μs** | **0.0133 μs** | **0.0153 μs** | **367,058.6** | **0.9669** | **0.0109** |   **7.91 KB** |
| **Decide** | **True**     | **1.441 μs** | **0.0893 μs** | **0.1029 μs** | **693,784.8** | **0.4233** |      **-** |   **3.49 KB** |
