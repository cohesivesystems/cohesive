```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Dry    : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method | Shape      | Mean     | Error | Allocated |
|------- |----------- |---------:|------:|----------:|
| **Map**    | **collection** | **7.898 ms** |    **NA** |  **44.82 KB** |
| **Map**    | **flat**       | **6.331 ms** |    **NA** |  **14.24 KB** |
| **Map**    | **large**      | **6.560 ms** |    **NA** | **108.22 KB** |
| **Map**    | **nested**     | **6.323 ms** |    **NA** |  **26.25 KB** |
