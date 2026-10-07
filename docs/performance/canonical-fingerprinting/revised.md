```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method           | Categories       | Shape      | Mean          | Error         | StdDev        | Gen0     | Gen1     | Gen2     | Allocated  |
|----------------- |----------------- |----------- |--------------:|--------------:|--------------:|---------:|---------:|---------:|-----------:|
| **CreateAndReuse**   | **AuthoredFirstUse** | **collection** |    **756.266 μs** |    **197.983 μs** |    **10.8521 μs** |        **-** |        **-** |        **-** |  **200.48 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **flat**       |     **15.801 μs** |     **35.866 μs** |     **1.9659 μs** |        **-** |        **-** |        **-** |    **4.17 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **large**      | **14,891.474 μs** | **65,348.550 μs** | **3,581.9746 μs** | **500.0000** | **125.0000** | **125.0000** | **6947.04 KB** |
| **CreateAndReuse**   | **AuthoredFirstUse** | **nested**     |     **19.965 μs** |     **32.724 μs** |     **1.7937 μs** |        **-** |        **-** |        **-** |   **15.89 KB** |
|                  |                  |            |               |               |               |          |          |          |            |
| **ImportAndCompute** | **ImportedFirstUse** | **collection** |    **171.550 μs** |     **30.227 μs** |     **1.6568 μs** |        **-** |        **-** |        **-** |  **152.32 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **flat**       |      **5.266 μs** |     **16.703 μs** |     **0.9155 μs** |        **-** |        **-** |        **-** |    **2.73 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **large**      |  **5,193.959 μs** |  **4,299.371 μs** |   **235.6630 μs** | **375.0000** |  **62.5000** |  **62.5000** |  **4946.2 KB** |
| **ImportAndCompute** | **ImportedFirstUse** | **nested**     |     **11.483 μs** |     **19.074 μs** |     **1.0455 μs** |        **-** |        **-** |        **-** |    **8.83 KB** |
