```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | Shape      | Mean       | Error       | StdDev     | Gen0    | Gen1   | Allocated |
|------- |----------- |-----------:|------------:|-----------:|--------:|-------:|----------:|
| **Map**    | **collection** |  **36.559 μs** |  **36.9992 μs** |  **2.0281 μs** | **11.3525** | **0.2441** |  **92.88 KB** |
| **Map**    | **flat**       |   **2.642 μs** |   **0.3277 μs** |  **0.0180 μs** |  **0.8469** |      **-** |   **6.95 KB** |
| **Map**    | **large**      | **146.514 μs** | **293.7613 μs** | **16.1020 μs** | **43.9453** | **2.4414** | **361.97 KB** |
| **Map**    | **nested**     |  **15.577 μs** |  **11.9062 μs** |  **0.6526 μs** |  **5.0659** | **0.0610** |  **41.87 KB** |
