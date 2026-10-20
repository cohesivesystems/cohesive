```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
IterationTime=250ms  LaunchCount=1  WarmupCount=3  

```
| Method  | Rows | Mean         | Error        | StdDev      | Gen0    | Gen1    | Allocated |
|-------- |----- |-------------:|-------------:|------------:|--------:|--------:|----------:|
| **Project** | **1**    |     **896.6 ns** |     **375.4 ns** |    **20.58 ns** |  **0.1140** |       **-** |     **968 B** |
| **Project** | **128**  | **108,298.5 ns** |  **40,614.5 ns** | **2,226.22 ns** | **10.7021** |  **2.1404** |   **90380 B** |
| **Project** | **1024** | **864,241.7 ns** | **172,887.4 ns** | **9,476.54 ns** | **85.5263** | **42.7632** |  **721191 B** |
