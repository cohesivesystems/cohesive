```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method | Shape      | Mean      | Error       | StdDev    | Gen0    | Gen1   | Allocated |
|------- |----------- |----------:|------------:|----------:|--------:|-------:|----------:|
| **Map**    | **collection** | **21.094 μs** |   **4.8155 μs** | **0.2640 μs** |  **4.2725** | **0.0610** |  **35.28 KB** |
| **Map**    | **flat**       |  **2.759 μs** |   **0.5714 μs** | **0.0313 μs** |  **0.8392** |      **-** |   **6.88 KB** |
| **Map**    | **large**      | **66.731 μs** | **144.2261 μs** | **7.9055 μs** | **11.9629** | **0.7324** |  **98.11 KB** |
| **Map**    | **nested**     | **10.033 μs** |   **2.4520 μs** | **0.1344 μs** |  **2.2583** | **0.0305** |  **18.47 KB** |
