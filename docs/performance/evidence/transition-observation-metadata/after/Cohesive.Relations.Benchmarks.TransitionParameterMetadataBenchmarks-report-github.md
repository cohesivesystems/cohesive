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
| **Decide** | **False**    | **2.509 μs** | **0.0051 μs** | **0.0063 μs** | **398,601.1** | **0.9013** | **0.0100** |   **7.39 KB** |
| **Decide** | **True**     | **1.291 μs** | **0.0085 μs** | **0.0098 μs** | **774,549.3** | **0.3838** |      **-** |   **3.17 KB** |
