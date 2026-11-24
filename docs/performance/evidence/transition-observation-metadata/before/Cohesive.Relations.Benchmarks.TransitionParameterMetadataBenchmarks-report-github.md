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
| **Decide** | **False**    | **2.714 μs** | **0.0074 μs** | **0.0082 μs** | **368,442.7** | **0.9617** | **0.0111** |   **7.91 KB** |
| **Decide** | **True**     | **1.319 μs** | **0.0062 μs** | **0.0069 μs** | **758,428.8** | **0.4266** |      **-** |   **3.49 KB** |
