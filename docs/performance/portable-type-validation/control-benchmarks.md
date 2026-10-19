```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method   | Fields | Shape      | Mean         | Error        | StdDev    | Gen0   | Gen1   | Allocated |
|--------- |------- |----------- |-------------:|-------------:|----------:|-------:|-------:|----------:|
| **Contract** | **1**      | **collection** |    **100.31 ns** |     **3.118 ns** |  **0.171 ns** | **0.1147** | **0.0002** |     **960 B** |
| Value    | 1      | collection |    198.17 ns |    53.182 ns |  2.915 ns | 0.1538 | 0.0002 |    1288 B |
| **Contract** | **1**      | **flat**       |     **63.58 ns** |    **23.373 ns** |  **1.281 ns** | **0.0755** |      **-** |     **632 B** |
| Value    | 1      | flat       |    100.11 ns |     5.958 ns |  0.327 ns | 0.0966 | 0.0001 |     808 B |
| **Contract** | **1**      | **nested**     |     **95.02 ns** |    **13.570 ns** |  **0.744 ns** | **0.1147** | **0.0002** |     **960 B** |
| Value    | 1      | nested     |    139.25 ns |    18.863 ns |  1.034 ns | 0.1175 |      - |     984 B |
| **Contract** | **128**    | **collection** |  **8,345.26 ns** | **1,714.725 ns** | **93.990 ns** | **6.3171** | **0.1221** |   **52869 B** |
| Value    | 128    | collection | 17,862.85 ns |   455.082 ns | 24.945 ns | 7.0801 | 0.1221 |   59295 B |
| **Contract** | **128**    | **flat**       |  **4,880.63 ns** |   **143.368 ns** |  **7.858 ns** | **3.6087** | **0.0687** |   **30187 B** |
| Value    | 128    | flat       |  7,238.94 ns |   182.697 ns | 10.014 ns | 3.9902 | 0.0763 |   33412 B |
| **Contract** | **128**    | **nested**     |  **7,920.45 ns** | **1,235.805 ns** | **67.739 ns** | **6.3171** | **0.1221** |   **52869 B** |
| Value    | 128    | nested     | 12,689.20 ns |   698.436 ns | 38.284 ns | 6.6833 | 0.1221 |   55943 B |
