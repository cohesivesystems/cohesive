```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  Job-YZJABG : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

IterationCount=8  LaunchCount=1  WarmupCount=3  

```
| Method              | Entries | Mean         | Error       | StdDev    | Ratio | Gen0    | Gen1    | Allocated | Alloc Ratio |
|-------------------- |-------- |-------------:|------------:|----------:|------:|--------:|--------:|----------:|------------:|
| **ReferenceOnly**       | **1**       |     **793.6 ns** |     **9.27 ns** |   **4.12 ns** |  **1.00** |  **0.2136** |       **-** |   **1.75 KB** |        **1.00** |
| StructuredLocations | 1       |   1,187.2 ns |     1.59 ns |   0.57 ns |  1.50 |  0.2289 |       - |   1.88 KB |        1.08 |
|                     |         |              |             |           |       |         |         |           |             |
| **ReferenceOnly**       | **16**      |  **11,100.2 ns** |    **55.41 ns** |  **24.60 ns** |  **1.00** |  **3.2654** |  **0.0153** |   **26.7 KB** |        **1.00** |
| StructuredLocations | 16      |  17,441.8 ns |   141.35 ns |  62.76 ns |  1.57 |  3.4180 |       - |  28.92 KB |        1.08 |
|                     |         |              |             |           |       |         |         |           |             |
| **ReferenceOnly**       | **256**     | **207,073.6 ns** |   **396.98 ns** | **207.63 ns** |  **1.00** | **79.5898** |  **6.8359** | **650.46 KB** |        **1.00** |
| StructuredLocations | 256     | 304,493.9 ns | 1,446.19 ns | 642.12 ns |  1.47 | 83.9844 | 12.2070 |  686.5 KB |        1.06 |
