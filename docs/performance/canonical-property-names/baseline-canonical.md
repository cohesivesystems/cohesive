```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method       | Shape    | Mean         | Error         | StdDev        | Median       | Gen0     | Gen1     | Gen2    | Allocated |
|------------- |--------- |-------------:|--------------:|--------------:|-------------:|---------:|---------:|--------:|----------:|
| **Canonicalize** | **escaped**  | **687,090.6 ns** |  **29,024.72 ns** |  **15,180.49 ns** | **687,938.5 ns** | **218.7500** | **218.7500** | **62.5000** |  **787222 B** |
| **Canonicalize** | **flat**     |     **146.9 ns** |      **43.75 ns** |      **22.88 ns** |     **141.9 ns** |        **-** |        **-** |       **-** |     **542 B** |
| **Canonicalize** | **repeated** | **684,189.5 ns** |  **32,945.76 ns** |  **14,628.12 ns** | **680,163.4 ns** | **218.7500** | **218.7500** | **62.5000** |  **726018 B** |
| **Canonicalize** | **unique**   | **442,467.4 ns** | **445,764.65 ns** | **233,143.53 ns** | **280,095.0 ns** |  **31.2500** |        **-** |       **-** |  **369481 B** |
