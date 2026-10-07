```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method           | Categories       | Shape      | Mean          | Error         | StdDev        | Median        | Gen0      | Gen1     | Gen2     | Allocated   |
|----------------- |----------------- |----------- |--------------:|--------------:|--------------:|--------------:|----------:|---------:|---------:|------------:|
| **CreateAndReuse**   | **AuthoredFirstUse** | **collection** |    **946.807 μs** |    **932.983 μs** |    **51.1399 μs** |    **966.399 μs** |         **-** |        **-** |        **-** |   **330.37 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **flat**       |     **17.296 μs** |     **21.819 μs** |     **1.1960 μs** |     **17.882 μs** |         **-** |        **-** |        **-** |     **5.36 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **large**      | **17,524.349 μs** | **85,385.383 μs** | **4,680.2610 μs** | **15,410.973 μs** | **1125.0000** | **375.0000** | **375.0000** | **11221.61 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **nested**     |     **23.051 μs** |     **57.601 μs** |     **3.1573 μs** |     **22.047 μs** |         **-** |        **-** |        **-** |     **22.3 KB** |
|                  |                  |            |               |               |               |               |           |          |          |             |
| **ImportAndCompute** | **ImportedFirstUse** | **collection** |    **208.508 μs** |     **17.863 μs** |     **0.9791 μs** |    **208.824 μs** |         **-** |        **-** |        **-** |   **282.21 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **flat**       |      **5.348 μs** |      **4.537 μs** |     **0.2487 μs** |      **5.349 μs** |         **-** |        **-** |        **-** |     **3.87 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **large**      |  **7,345.142 μs** | **11,058.733 μs** |   **606.1665 μs** |  **7,422.038 μs** |  **875.0000** | **187.5000** | **187.5000** |  **9219.31 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **nested**     |     **23.982 μs** |    **333.247 μs** |    **18.2664 μs** |     **13.883 μs** |         **-** |        **-** |        **-** |    **15.12 KB** |
