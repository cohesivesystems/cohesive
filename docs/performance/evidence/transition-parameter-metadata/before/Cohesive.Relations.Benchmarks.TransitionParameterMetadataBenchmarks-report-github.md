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
| **Decide** | **False**    | **2.779 μs** | **0.0104 μs** | **0.0120 μs** | **359,896.3** | **1.0426** | **0.0111** |    **8.6 KB** |
| **Decide** | **True**     | **1.336 μs** | **0.0118 μs** | **0.0131 μs** | **748,345.2** | **0.4358** |      **-** |   **3.57 KB** |
